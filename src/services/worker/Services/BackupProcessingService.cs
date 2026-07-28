using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Events;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Logging.OpenTelemetry;

namespace FlorisDeV.BackupWorker.Services;

public interface IBackupProcessingService
{
    Task ProcessBackupRunAsync(BackupRunCommittedEvent backupEvent, CancellationToken cancellationToken = default);
}

public partial class BackupProcessingService(
    ILogger<BackupProcessingService> logger,
    IBlobStorageService blobStorageService,
    IManifestManager manifestManager,
    IConfiguration configuration,
    TelemetryProvider telemetry
) : IBackupProcessingService
{
    /// <summary>Entries between commit-job progress publications.</summary>
    private const int CheckpointEveryEntries = 100;

    private readonly int _maxCommitAttempts = Math.Max(1, configuration.GetValue("CommitProcessing:MaxAttempts", 5));
    private readonly double _maxFailurePercentage = Math.Max(0, configuration.GetValue("CommitProcessing:MaxFailurePercentage", 5.0));

    /// <summary>
    /// Manifest entries processed concurrently. Each entry is a short chain of round trips, so this
    /// trades directly against the state store's request-unit ceiling rather than against CPU: raise it
    /// only alongside the provisioned throughput, or the extra concurrency just produces throttling.
    /// </summary>
    private readonly int _maxParallelFiles = Math.Max(1, configuration.GetValue("CommitProcessing:MaxParallelFiles", 8));

    public async Task ProcessBackupRunAsync(BackupRunCommittedEvent backupEvent, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("ProcessBackupRun");
        activity?.SetTag(ActivityAttributes.OperationName, "ProcessBackupRun");
        activity?.SetTag(ActivityAttributes.DeviceId, backupEvent.DeviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, backupEvent.RunId.ToString());
        activity?.SetTag("commit_id", backupEvent.CommitId.ToString());

        var stopwatch = Stopwatch.StartNew();
        var metricTags = new TagList { { "operation", "process_backup" } };

        try
        {
            LogProcessingStarted(logger, backupEvent.DeviceId, backupEvent.RunId, backupEvent.StagingPath);

            // Atomically claim the queued commit job before doing any work. If another worker
            // claimed it first, skip this delivery and let that worker finish.
            var (claimed, commitJob) = await manifestManager.TryClaimCommitJobAsync(backupEvent.CommitId, cancellationToken);
            if (!claimed)
            {
                LogAlreadyProcessed(logger, backupEvent.DeviceId, backupEvent.RunId, commitJob.Status);
                activity?.SetTag(ActivityAttributes.OperationStatus, "skipped");
                activity?.SetTag("skip_reason", commitJob.Status.ToString());
                return;
            }

            // Update BackupRun status to Processing
            var run = await manifestManager.GetBackupRunAsync(backupEvent.DeviceId, backupEvent.RunId, cancellationToken);
            run.Status = BackupRunStatus.Processing;
            await manifestManager.UpdateBackupRunAsync(
                backupEvent.DeviceId,
                backupEvent.RunId,
                run,
                cancellationToken);

            // Download and parse run-manifest.json. Derive the path from the event identity
            // instead of trusting ManifestPath from the message body.
            var manifestPath = GetManifestPath(backupEvent.DeviceId, backupEvent.RunId);

            if (!await ManifestExistsAsync(manifestPath, cancellationToken))
            {
                throw new InvalidOperationException($"Run manifest not found at {manifestPath}");
            }

            // Persists the manifest as the durable record the ops/status endpoint reads once the temporary
            // blob copy is cleaned up below. Runs with enough files can exceed Cosmos's per-document size
            // limit here; that must not abort processing of an otherwise-valid backup run, so on failure we
            // keep the blob copy around instead (see manifestPersisted below).
            // This pass also yields the entry counts, which the progress reporting below needs up front.
            var (manifestPersisted, fileCount, deletedCount) =
                await TryPersistRunManifestAsync(backupEvent.DeviceId, backupEvent.RunId, manifestPath, cancellationToken);

            LogManifestLoaded(logger, backupEvent.DeviceId, backupEvent.RunId, fileCount, deletedCount);

            // Record the total up front so commit-status polling can report progress.
            commitJob.TotalFiles = fileCount + deletedCount;
            commitJob = await manifestManager.UpdateCommitJobAsync(commitJob, cancellationToken);

            var containerClient = await blobStorageService.GetContainerClientAsync(cancellationToken);
            var counters = new ProcessingCounters();
            var checkpoint = new CommitJobCheckpoint(commitJob, manifestManager);

            // Second streamed pass: entries are processed as they are read, so the manifest is never
            // held in memory even for a run with hundreds of thousands of files.
            await Parallel.ForEachAsync(
                StreamManifestAsync(manifestPath, cancellationToken),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _maxParallelFiles,
                    CancellationToken = cancellationToken
                },
                async (item, token) =>
                {
                    if (item.File is { } fileEntry)
                    {
                        counters.CountFileSeen();

                        try
                        {
                            await ProcessFileEntryAsync(
                                backupEvent.DeviceId,
                                backupEvent.RunId,
                                commitJob.CommitId,
                                fileEntry,
                                containerClient,
                                token);
                            counters.CountProcessed();
                        }
                        catch (StagedBlobValidationException ex)
                        {
                            counters.CountSkipped();
                            LogStagedBlobSkipped(logger, backupEvent.DeviceId, backupEvent.RunId, fileEntry.LogicalPath, ex.Message);
                        }
                    }
                    else if (item.DeletedPath is { } deletedPath)
                    {
                        await ProcessFileDeletionAsync(
                            backupEvent.DeviceId,
                            backupEvent.RunId,
                            deletedPath,
                            containerClient,
                            token);
                        counters.CountProcessed();
                    }

                    await checkpoint.RecordAsync(counters.Processed, counters.Skipped, token);
                });

            var processedCount = counters.Processed;
            var skippedFiles = counters.Skipped;
            var filesSeen = counters.FilesSeen;
            commitJob = checkpoint.Current;

            // Too many skipped files means the run is not trustworthy - fail it loudly so it is retried
            // (and surfaced) rather than silently recording a mostly-empty backup.
            var failurePercentage = filesSeen == 0
                ? 0
                : skippedFiles * 100.0 / filesSeen;
            if (failurePercentage > _maxFailurePercentage)
            {
                throw new InvalidOperationException(
                    $"Backup run failed: {skippedFiles}/{filesSeen} files failed staged-content validation " +
                    $"({failurePercentage:F1}%), exceeding the {_maxFailurePercentage}% threshold.");
            }

            var terminalStatus = skippedFiles > 0 ? BackupRunStatus.CompletedWithErrors : BackupRunStatus.Succeeded;

            LogProcessingCompleted(logger, backupEvent.DeviceId, backupEvent.RunId, processedCount);

            // Update run status in manifest
            await UpdateBackupRunStatusAsync(
                backupEvent.DeviceId,
                backupEvent.RunId,
                terminalStatus,
                filesSeen - skippedFiles,
                cancellationToken);

            // Update CommitJob terminal status
            commitJob.Status = skippedFiles > 0 ? CommitJobStatus.CompletedWithErrors : CommitJobStatus.Succeeded;
            commitJob.FilesProcessed = processedCount;
            commitJob.FilesFailed = skippedFiles;
            commitJob.Error = skippedFiles > 0
                ? $"{skippedFiles} file(s) skipped: staged content did not match the manifest (source changed during backup)."
                : null;
            commitJob.CompletedAt = DateTimeOffset.UtcNow;
            await manifestManager.UpdateCommitJobAsync(commitJob, cancellationToken);

            await TryCleanupRunTemporaryBlobsAsync(
                backupEvent.DeviceId,
                backupEvent.RunId,
                containerClient,
                deleteManifestBlob: manifestPersisted,
                cancellationToken);

            stopwatch.Stop();
            telemetry.BackupRunsProcessed.Add(1, metricTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag(ActivityAttributes.BackupRunStatus, BackupRunStatus.Succeeded.ToString());
            activity?.SetTag("backup.files_processed", processedCount);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var errorTags = new TagList
            {
                { "operation", "process_backup" },
                { "error.type", ex.GetType().Name }
            };
            telemetry.BackupRunsFailed.Add(1, errorTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, errorTags);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(ActivityAttributes.OperationStatus, "error");
            activity?.SetTag(ActivityAttributes.ErrorType, ex.GetType().Name);
            activity?.SetTag(ActivityAttributes.ErrorMessage, ex.Message);
            activity?.AddException(ex);

            LogProcessingFailed(logger, backupEvent.DeviceId, backupEvent.RunId, ex);

            // Update run status to Failed
            try
            {
                await UpdateBackupRunStatusAsync(
                    backupEvent.DeviceId,
                    backupEvent.RunId,
                    BackupRunStatus.Failed,
                    0,
                    cancellationToken);

                // Update CommitJob status to Failed
                var commitJob = await manifestManager.GetCommitJobAsync(backupEvent.CommitId, cancellationToken);
                commitJob.Status = CommitJobStatus.Failed;
                commitJob.FilesFailed++;
                commitJob.Error = ex.Message;
                commitJob.FailureCategory = ClassifyFailure(ex);
                commitJob.LastErrorAt = DateTimeOffset.UtcNow;
                commitJob.NextRetryAt = commitJob.AttemptCount < _maxCommitAttempts
                    ? DateTimeOffset.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, commitJob.AttemptCount))))
                    : null;
                commitJob.DeadLetteredAt = commitJob.AttemptCount >= _maxCommitAttempts
                    ? DateTimeOffset.UtcNow
                    : null;
                commitJob.CompletedAt = DateTimeOffset.UtcNow;
                await manifestManager.UpdateCommitJobAsync(commitJob, cancellationToken);
            }
            catch (Exception updateEx)
            {
                LogFailedToUpdateStatus(logger, backupEvent.DeviceId, backupEvent.RunId, updateEx);
            }

            throw;
        }
    }

    /// <summary>
    /// Thread-safe tallies for a run's entries, shared across the concurrent processing of a manifest.
    /// </summary>
    private sealed class ProcessingCounters
    {
        private int _processed;
        private int _skipped;
        private int _filesSeen;

        public int Processed => Volatile.Read(ref _processed);
        public int Skipped => Volatile.Read(ref _skipped);
        public int FilesSeen => Volatile.Read(ref _filesSeen);

        public void CountProcessed() => Interlocked.Increment(ref _processed);
        public void CountSkipped() => Interlocked.Increment(ref _skipped);
        public void CountFileSeen() => Interlocked.Increment(ref _filesSeen);
    }

    /// <summary>
    /// Publishes progress onto the commit job so status polling can see it during a long run.
    ///
    /// The commit job is a single document updated with optimistic concurrency, so concurrent writers
    /// would fight over its ETag. Only one checkpoint is ever in flight: any entry that finishes while
    /// a write is in progress skips its own, which is harmless because progress reporting is advisory
    /// and the terminal status update writes the final counts.
    /// </summary>
    private sealed class CommitJobCheckpoint(CommitJob commitJob, IManifestManager manifestManager)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private CommitJob _current = commitJob;
        private int _lastPublishedTotal;

        public CommitJob Current => _current;

        public async Task RecordAsync(int processedCount, int skippedFiles, CancellationToken cancellationToken)
        {
            var total = processedCount + skippedFiles;
            if (total - Volatile.Read(ref _lastPublishedTotal) < CheckpointEveryEntries)
            {
                return;
            }

            if (!await _gate.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                if (total - _lastPublishedTotal < CheckpointEveryEntries)
                {
                    return;
                }

                _current.FilesProcessed = processedCount;
                _current.FilesFailed = skippedFiles;
                _current = await manifestManager.UpdateCommitJobAsync(_current, cancellationToken);
                Volatile.Write(ref _lastPublishedTotal, total);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static string ClassifyFailure(Exception ex)
        => ex switch
        {
            JsonException => "ManifestInvalid",
            StagedBlobValidationException => "StagedBlobInvalid",
            RequestFailedException requestFailedException when requestFailedException.Status is 408 or 429 or >= 500 => "TransientStorage",
            RequestFailedException requestFailedException when requestFailedException.Status is 404 => "MissingBlob",
            InvalidOperationException invalidOperationException when invalidOperationException.Message.Contains("manifest", StringComparison.OrdinalIgnoreCase) => "ManifestInvalid",
            InvalidOperationException invalidOperationException when invalidOperationException.Message.Contains("Staged blob", StringComparison.OrdinalIgnoreCase) => "StagedBlobInvalid",
            _ => ex.GetType().Name
        };

    private async Task<bool> ManifestExistsAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var containerClient = await blobStorageService.GetContainerClientAsync(cancellationToken);
        return await containerClient.GetBlobClient(manifestPath).ExistsAsync(cancellationToken);
    }

    /// <summary>
    /// Streams the run manifest's entries, reading the blob through a fixed-size buffer rather than
    /// downloading and deserializing it whole: a run covering hundreds of thousands of files produces
    /// a manifest far too large to materialize inside the container's memory limit. Each call
    /// re-opens the blob, so the manifest can be walked more than once without being held in memory.
    /// </summary>
    private async IAsyncEnumerable<RunManifestStreamItem> StreamManifestAsync(
        string manifestPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var containerClient = await blobStorageService.GetContainerClientAsync(cancellationToken);
        var blobClient = containerClient.GetBlobClient(manifestPath);

        await using var stream = await blobClient.OpenReadAsync(cancellationToken: cancellationToken);

        await foreach (var item in RunManifestStreamReader.ReadAsync(stream, cancellationToken))
        {
            yield return item;
        }
    }

    private async Task ProcessFileEntryAsync(
        Guid deviceId,
        Guid runId,
        Guid commitId,
        ManifestFileEntry fileEntry,
        BlobContainerClient containerClient,
        CancellationToken cancellationToken)
    {
        var logicalPath = fileEntry.LogicalPath;
        LogProcessingFileEntry(logger, logicalPath, fileEntry.UniqueFileId);

        var sourceBlobName = $"staging/{deviceId:N}/{runId:N}/{fileEntry.UniqueFileId}";
        var destinationBlobName = $"devices/{deviceId:N}/files/{fileEntry.UniqueFileId}";

        var progress = await manifestManager.GetCommitFileProgressAsync(commitId, fileEntry.UniqueFileId, cancellationToken);

        if (progress?.Status == CommitFileStatus.Succeeded)
        {
            LogCommitFileAlreadySucceeded(logger, commitId, fileEntry.UniqueFileId, logicalPath);
            return;
        }

        try
        {
            await ValidateStagedBlobAsync(containerClient, sourceBlobName, destinationBlobName, fileEntry, cancellationToken);
            try
            {
                // Move blob from staging to files/
                await blobStorageService.MoveBlobAsync(sourceBlobName, destinationBlobName, null, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                if (!await IsDestinationBlobPresentAsync(containerClient, destinationBlobName, cancellationToken))
                {
                    throw;
                }

                LogSourceMissingDestinationPresent(logger, sourceBlobName, destinationBlobName, ex);
            }

            // Check if file already exists
            var existingFile = await manifestManager.GetFileEntryAsync(deviceId, logicalPath, cancellationToken);

            // Create new FileVersion (Active)
            var newVersion = new FileVersion
            {
                DeviceId = deviceId,
                UniqueFileId = fileEntry.UniqueFileId,
                RelativePath = logicalPath,
                Sha256 = fileEntry.Sha256,
                Size = fileEntry.Size,
                Encryption = fileEntry.Encryption,
                CreatedAt = DateTimeOffset.UtcNow,
                State = FileVersionState.Active
            };
            await manifestManager.SaveFileVersionAsync(newVersion, cancellationToken);

            // If file existed before, retire the old version
            if (existingFile != null && !existingFile.IsDeleted)
            {
                await RetireFileVersionAsync(
                    deviceId,
                    existingFile.CurrentVersionId,
                    containerClient,
                    cancellationToken);
            }

            // Update/create FileEntry
            var fileEntryRecord = new FileEntry
            {
                DeviceId = deviceId,
                RelativePath = logicalPath,
                CurrentVersionId = fileEntry.UniqueFileId,
                Size = fileEntry.Size,
                LastWriteUtc = fileEntry.Mtime,
                LastBackupRunId = runId.ToString("N"),
                IsDeleted = false,
                ETag = existingFile?.ETag // Preserve ETag if updating
            };
            await manifestManager.SaveFileEntryAsync(fileEntryRecord, cancellationToken);

            await SaveTerminalProgressAsync(
                progress, commitId, deviceId, runId, fileEntry, logicalPath,
                CommitFileStatus.Succeeded, error: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SaveTerminalProgressAsync(
                progress, commitId, deviceId, runId, fileEntry, logicalPath,
                CommitFileStatus.Failed, ex.Message, cancellationToken);
            throw;
        }

        LogFileEntryProcessed(logger, logicalPath, fileEntry.UniqueFileId);
    }

    /// <summary>
    /// Writes the file's terminal outcome, reusing the record an earlier attempt left behind so its
    /// ETag is carried forward, or creating one when this is the first attempt.
    /// </summary>
    private Task SaveTerminalProgressAsync(
        CommitFileProgress? existing,
        Guid commitId,
        Guid deviceId,
        Guid runId,
        ManifestFileEntry fileEntry,
        string logicalPath,
        CommitFileStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        var progress = existing ?? new CommitFileProgress
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            UniqueFileId = fileEntry.UniqueFileId,
            LogicalPath = logicalPath
        };

        progress.Status = status;
        progress.Error = error;
        progress.UpdatedAt = DateTimeOffset.UtcNow;

        return manifestManager.SaveCommitFileProgressAsync(progress, cancellationToken);
    }

    private static async Task ValidateStagedBlobAsync(
        BlobContainerClient containerClient,
        string sourceBlobName,
        string destinationBlobName,
        ManifestFileEntry fileEntry,
        CancellationToken cancellationToken)
    {
        var sourceBlobClient = containerClient.GetBlobClient(sourceBlobName);
        BlobProperties properties;

        try
        {
            properties = await sourceBlobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            if (await IsDestinationBlobPresentAsync(containerClient, destinationBlobName, cancellationToken))
            {
                return;
            }

            throw new StagedBlobValidationException($"Staged blob not found: {sourceBlobName}", ex);
        }

        if (properties.ContentLength != fileEntry.Size)
        {
            throw new StagedBlobValidationException(
                $"Staged blob size mismatch for '{fileEntry.LogicalPath}' ({fileEntry.UniqueFileId}). " +
                $"Expected {fileEntry.Size} bytes, actual {properties.ContentLength} bytes.");
        }

        if (!TryGetMetadataValue(properties.Metadata, BackupBlobMetadata.Sha256, out var uploadedSha256))
        {
            throw new StagedBlobValidationException(
                $"Staged blob is missing required metadata '{BackupBlobMetadata.Sha256}' for '{fileEntry.LogicalPath}' ({fileEntry.UniqueFileId}).");
        }

        if (!string.Equals(uploadedSha256, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StagedBlobValidationException(
                $"Staged blob SHA-256 metadata mismatch for '{fileEntry.LogicalPath}' ({fileEntry.UniqueFileId}).");
        }
    }

    private static async Task<bool> IsDestinationBlobPresentAsync(
        BlobContainerClient containerClient,
        string destinationBlobName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await containerClient.GetBlobClient(destinationBlobName).ExistsAsync(cancellationToken);
        }
        catch (RequestFailedException)
        {
            return false;
        }
    }

    private static bool TryGetMetadataValue(
        IDictionary<string, string> metadata,
        string key,
        out string value)
    {
        foreach (var item in metadata)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private async Task ProcessFileDeletionAsync(
        Guid deviceId,
        Guid runId,
        string relativePath,
        BlobContainerClient containerClient,
        CancellationToken cancellationToken)
    {
        LogProcessingFileDeletion(logger, relativePath);

        // Load existing FileEntry
        var existingFile = await manifestManager.GetFileEntryAsync(deviceId, relativePath, cancellationToken);

        if (existingFile == null || existingFile.IsDeleted)
        {
            LogFileAlreadyDeleted(logger, relativePath);
            return; // Already deleted or doesn't exist
        }

        // Retire the current version
        await RetireFileVersionAsync(deviceId, existingFile.CurrentVersionId, containerClient, cancellationToken);

        // Mark FileEntry as deleted
        existingFile = existingFile with { IsDeleted = true };
        await manifestManager.SaveFileEntryAsync(existingFile, cancellationToken);

        LogFileDeletionProcessed(logger, relativePath);
    }

    /// <summary>
    /// Streams the manifest into chunked storage, reporting whether it was persisted along with the
    /// entry counts the caller needs for progress reporting. When persistence fails the counts are
    /// still established with a read-only pass, so an otherwise-valid run can continue.
    /// </summary>
    private async Task<(bool Persisted, int FileCount, int DeletedCount)> TryPersistRunManifestAsync(
        Guid deviceId,
        Guid runId,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var (fileCount, deletedCount) = await manifestManager.SaveRunManifestAsync(
                deviceId, runId, StreamManifestAsync(manifestPath, cancellationToken), cancellationToken);

            return (true, fileCount, deletedCount);
        }
        catch (Exception ex)
        {
            LogRunManifestPersistFailed(logger, deviceId, runId, ex);

            var files = 0;
            var deletions = 0;

            await foreach (var item in StreamManifestAsync(manifestPath, cancellationToken))
            {
                if (item.File is not null)
                {
                    files++;
                }
                else
                {
                    deletions++;
                }
            }

            return (false, files, deletions);
        }
    }

    private async Task TryCleanupRunTemporaryBlobsAsync(
        Guid deviceId,
        Guid runId,
        BlobContainerClient containerClient,
        bool deleteManifestBlob,
        CancellationToken cancellationToken)
    {
        try
        {
            var stagingPrefix = $"staging/{deviceId:N}/{runId:N}/";
            var deletedStagingBlobCount = 0;

            await foreach (var blob in containerClient.GetBlobsAsync(prefix: stagingPrefix, cancellationToken: cancellationToken))
            {
                await containerClient.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
                deletedStagingBlobCount++;
            }

            // If the Cosmos manifest copy failed to persist (e.g. too large), the blob is the only
            // remaining durable record of this run's files - keep it instead of deleting it here.
            var manifestDeleted = false;
            if (deleteManifestBlob)
            {
                var manifestPath = GetManifestPath(deviceId, runId);
                manifestDeleted = (await containerClient.GetBlobClient(manifestPath).DeleteIfExistsAsync(cancellationToken: cancellationToken)).Value;
            }

            LogRunTemporaryBlobCleanupCompleted(logger, deviceId, runId, deletedStagingBlobCount, manifestDeleted);
        }
        catch (Exception ex)
        {
            LogRunTemporaryBlobCleanupFailed(logger, deviceId, runId, ex);
        }
    }

    private async Task RetireFileVersionAsync(
        Guid deviceId,
        string uniqueFileId,
        BlobContainerClient containerClient,
        CancellationToken cancellationToken)
    {
        LogRetiringFileVersion(logger, uniqueFileId);

        // Load the FileVersion
        var fileVersion = await manifestManager.GetFileVersionAsync(deviceId, uniqueFileId, cancellationToken);

        if (fileVersion == null)
        {
            LogFileVersionNotFound(logger, uniqueFileId);
            return;
        }

        if (fileVersion.State == FileVersionState.Retired)
        {
            LogFileVersionAlreadyRetired(logger, uniqueFileId);
            return; // Already retired
        }

        // Move blob from files/ to retired/
        var sourceBlobName = $"devices/{deviceId:N}/files/{uniqueFileId}";
        var destinationBlobName = $"devices/{deviceId:N}/retired/{uniqueFileId}";

        // Tag retired blobs for lifecycle policy targeting
        var tags = new Dictionary<string, string>
        {
            { "state", "retired" },
            { "deviceId", deviceId.ToString("N") }
        };

        try
        {
            await blobStorageService.MoveBlobAsync(sourceBlobName, destinationBlobName, tags, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            if (!await IsDestinationBlobPresentAsync(containerClient, destinationBlobName, cancellationToken))
            {
                throw;
            }

            LogSourceMissingDestinationPresent(logger, sourceBlobName, destinationBlobName, ex);
        }

        // Update FileVersion state to Retired
        var retiredVersion = fileVersion with
        {
            State = FileVersionState.Retired,
            RetiredAt = DateTimeOffset.UtcNow
        };
        await manifestManager.SaveFileVersionAsync(retiredVersion, cancellationToken);

        LogFileVersionRetired(logger, uniqueFileId);
    }

    private async Task UpdateBackupRunStatusAsync(
        Guid deviceId,
        Guid runId,
        BackupRunStatus status,
        int filesProcessed,
        CancellationToken cancellationToken)
    {
        var run = await manifestManager.GetBackupRunAsync(deviceId, runId, cancellationToken);

        if (run == null)
        {
            throw new InvalidOperationException($"Backup run {runId} for device {deviceId} not found");
        }

        run.Status = status;
        run.FilesBackedUp = filesProcessed;
        run.CompletedAt ??= DateTimeOffset.UtcNow;

        await manifestManager.UpdateBackupRunAsync(deviceId, runId, run, cancellationToken);
    }

    #region Logging

    private static string GetManifestPath(Guid deviceId, Guid runId) => $"runs/{deviceId:N}/{runId:N}/run-manifest.json";

    [LoggerMessage(LogLevel.Information, "Started processing backup run {runId} for device {deviceId} from staging path: {stagingPath}")]
    static partial void LogProcessingStarted(ILogger logger, Guid deviceId, Guid runId, string stagingPath);

    [LoggerMessage(LogLevel.Information, "Loaded manifest for run {runId} device {deviceId}: {fileCount} files, {deletedCount} deletions")]
    static partial void LogManifestLoaded(ILogger logger, Guid deviceId, Guid runId, int fileCount, int deletedCount);

    [LoggerMessage(LogLevel.Warning, "Failed to persist manifest cache for run {runId} device {deviceId}; continuing with blob copy as source of truth.")]
    static partial void LogRunManifestPersistFailed(ILogger logger, Guid deviceId, Guid runId, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Skipped file '{logicalPath}' in run {runId} device {deviceId}: {reason}. It will be re-detected and retried on the next backup.")]
    static partial void LogStagedBlobSkipped(ILogger logger, Guid deviceId, Guid runId, string logicalPath, string reason);

    [LoggerMessage(LogLevel.Information, "Processing file entry: {relativePath} ({uniqueFileId})")]
    static partial void LogProcessingFileEntry(ILogger logger, string relativePath, string uniqueFileId);

    [LoggerMessage(LogLevel.Information, "File entry processed: {relativePath} ({uniqueFileId})")]
    static partial void LogFileEntryProcessed(ILogger logger, string relativePath, string uniqueFileId);

    [LoggerMessage(LogLevel.Information, "Processing file deletion: {relativePath}")]
    static partial void LogProcessingFileDeletion(ILogger logger, string relativePath);

    [LoggerMessage(LogLevel.Information, "File deletion processed: {relativePath}")]
    static partial void LogFileDeletionProcessed(ILogger logger, string relativePath);

    [LoggerMessage(LogLevel.Warning, "File already deleted or doesn't exist: {relativePath}")]
    static partial void LogFileAlreadyDeleted(ILogger logger, string relativePath);

    [LoggerMessage(LogLevel.Information, "Retiring file version: {uniqueFileId}")]
    static partial void LogRetiringFileVersion(ILogger logger, string uniqueFileId);

    [LoggerMessage(LogLevel.Information, "File version retired: {uniqueFileId}")]
    static partial void LogFileVersionRetired(ILogger logger, string uniqueFileId);

    [LoggerMessage(LogLevel.Warning, "File version not found: {uniqueFileId}")]
    static partial void LogFileVersionNotFound(ILogger logger, string uniqueFileId);

    [LoggerMessage(LogLevel.Warning, "File version already retired: {uniqueFileId}")]
    static partial void LogFileVersionAlreadyRetired(ILogger logger, string uniqueFileId);

    [LoggerMessage(LogLevel.Information, "Completed processing backup run {runId} for device {deviceId}. Processed {processedCount} items")]
    static partial void LogProcessingCompleted(ILogger logger, Guid deviceId, Guid runId, int processedCount);

    [LoggerMessage(LogLevel.Information, "Cleaned temporary blobs for backup run {runId} device {deviceId}. Deleted {deletedStagingBlobCount} staging blobs, manifestDeleted={manifestDeleted}")]
    static partial void LogRunTemporaryBlobCleanupCompleted(ILogger logger, Guid deviceId, Guid runId, int deletedStagingBlobCount, bool manifestDeleted);

    [LoggerMessage(LogLevel.Warning, "Failed to clean temporary blobs for backup run {runId} device {deviceId}. Backup processing remains succeeded.")]
    static partial void LogRunTemporaryBlobCleanupFailed(ILogger logger, Guid deviceId, Guid runId, Exception ex);

    [LoggerMessage(LogLevel.Error, "Failed to process backup run {runId} for device {deviceId}")]
    static partial void LogProcessingFailed(ILogger logger, Guid deviceId, Guid runId, Exception ex);

    [LoggerMessage(LogLevel.Error, "Failed to update status for backup run {runId} for device {deviceId}")]
    static partial void LogFailedToUpdateStatus(ILogger logger, Guid deviceId, Guid runId, Exception ex);

    [LoggerMessage(LogLevel.Information, "Backup run {runId} for device {deviceId} already processed with status: {status}. Skipping.")]
    static partial void LogAlreadyProcessed(ILogger logger, Guid deviceId, Guid runId, CommitJobStatus status);

    [LoggerMessage(LogLevel.Warning, "Backup run {runId} for device {deviceId} is being processed concurrently. Skipping this message.")]
    static partial void LogConcurrentProcessing(ILogger logger, Guid deviceId, Guid runId);

    [LoggerMessage(LogLevel.Information, "Commit file progress {commitId}/{uniqueFileId} for {logicalPath} already succeeded. Skipping.")]
    static partial void LogCommitFileAlreadySucceeded(ILogger logger, Guid commitId, string uniqueFileId, string logicalPath);

    [LoggerMessage(LogLevel.Warning, "Source blob {sourceBlobName} is missing, but destination blob {destinationBlobName} exists. Continuing idempotent move recovery.")]
    static partial void LogSourceMissingDestinationPresent(ILogger logger, string sourceBlobName, string destinationBlobName, Exception ex);

    #endregion
}
