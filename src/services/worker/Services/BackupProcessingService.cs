using System.Diagnostics;
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
    private readonly int _maxCommitAttempts = Math.Max(1, configuration.GetValue("CommitProcessing:MaxAttempts", 5));

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
            var manifest = await DownloadManifestAsync(manifestPath, cancellationToken);

            if (manifest == null)
            {
                throw new InvalidOperationException($"Run manifest not found at {manifestPath}");
            }

            LogManifestLoaded(logger, backupEvent.DeviceId, backupEvent.RunId, manifest.Files.Count, manifest.Deleted.Count);

            var containerClient = await blobStorageService.GetContainerClientAsync(cancellationToken);
            var processedCount = 0;

            // Process new/changed files
            foreach (var fileEntry in manifest.Files)
            {
                await ProcessFileEntryAsync(
                    backupEvent.DeviceId,
                    backupEvent.RunId,
                    commitJob.CommitId,
                    fileEntry,
                    containerClient,
                    cancellationToken);
                processedCount++;
            }

            // Process deleted files
            foreach (var deletedPath in manifest.Deleted)
            {
                await ProcessFileDeletionAsync(
                    backupEvent.DeviceId,
                    backupEvent.RunId,
                    deletedPath,
                    containerClient,
                    cancellationToken);
                processedCount++;
            }

            LogProcessingCompleted(logger, backupEvent.DeviceId, backupEvent.RunId, processedCount);

            // Update run status in manifest
            await UpdateBackupRunStatusAsync(
                backupEvent.DeviceId,
                backupEvent.RunId,
                BackupRunStatus.Succeeded,
                manifest.Files.Count,
                cancellationToken);

            // Update CommitJob status to Succeeded
            commitJob.Status = CommitJobStatus.Succeeded;
            commitJob.FilesProcessed = processedCount;
            commitJob.CompletedAt = DateTimeOffset.UtcNow;
            await manifestManager.UpdateCommitJobAsync(commitJob, cancellationToken);

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

    private static string ClassifyFailure(Exception ex)
        => ex switch
        {
            JsonException => "ManifestInvalid",
            RequestFailedException requestFailedException when requestFailedException.Status is 408 or 429 or >= 500 => "TransientStorage",
            RequestFailedException requestFailedException when requestFailedException.Status is 404 => "MissingBlob",
            InvalidOperationException invalidOperationException when invalidOperationException.Message.Contains("manifest", StringComparison.OrdinalIgnoreCase) => "ManifestInvalid",
            InvalidOperationException invalidOperationException when invalidOperationException.Message.Contains("Staged blob", StringComparison.OrdinalIgnoreCase) => "StagedBlobInvalid",
            _ => ex.GetType().Name
        };

    private async Task<RunManifest?> DownloadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var containerClient = await blobStorageService.GetContainerClientAsync(cancellationToken);
        var blobClient = containerClient.GetBlobClient(manifestPath);

        try
        {
            var downloadResponse = await blobClient.DownloadContentAsync(cancellationToken);
            var content = downloadResponse.Value.Content.ToString();

            var manifest = JsonSerializer.Deserialize<RunManifest>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return manifest;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
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

        var progress = await GetOrCreateFileProgressAsync(commitId, deviceId, runId, fileEntry, logicalPath, cancellationToken);

        if (progress.Status == CommitFileStatus.Succeeded)
        {
            LogCommitFileAlreadySucceeded(logger, commitId, fileEntry.UniqueFileId, logicalPath);
            return;
        }

        if (progress.Status == CommitFileStatus.StateUpdated)
        {
            await SaveFileProgressAsync(progress, CommitFileStatus.Succeeded, cancellationToken);
            return;
        }

        try
        {
            if (progress.Status == CommitFileStatus.Pending || progress.Status == CommitFileStatus.Failed)
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

                progress = await SaveFileProgressAsync(progress, CommitFileStatus.Moved, cancellationToken);
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

            progress = await SaveFileProgressAsync(progress, CommitFileStatus.StateUpdated, cancellationToken);
            await SaveFileProgressAsync(progress, CommitFileStatus.Succeeded, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress.Status = CommitFileStatus.Failed;
            progress.Error = ex.Message;
            await manifestManager.SaveCommitFileProgressAsync(progress, cancellationToken);
            throw;
        }

        LogFileEntryProcessed(logger, logicalPath, fileEntry.UniqueFileId);
    }

    private async Task<CommitFileProgress> GetOrCreateFileProgressAsync(
        Guid commitId,
        Guid deviceId,
        Guid runId,
        ManifestFileEntry fileEntry,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        var progress = await manifestManager.GetCommitFileProgressAsync(commitId, fileEntry.UniqueFileId, cancellationToken);
        if (progress != null)
        {
            return progress;
        }

        return await manifestManager.SaveCommitFileProgressAsync(new CommitFileProgress
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            UniqueFileId = fileEntry.UniqueFileId,
            LogicalPath = logicalPath,
            Status = CommitFileStatus.Pending,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task<CommitFileProgress> SaveFileProgressAsync(
        CommitFileProgress progress,
        CommitFileStatus status,
        CancellationToken cancellationToken)
    {
        progress.Status = status;
        progress.Error = null;
        return await manifestManager.SaveCommitFileProgressAsync(progress, cancellationToken);
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

            throw new InvalidOperationException($"Staged blob not found: {sourceBlobName}", ex);
        }

        if (properties.ContentLength != fileEntry.Size)
        {
            throw new InvalidOperationException(
                $"Staged blob size mismatch for '{fileEntry.LogicalPath}' ({fileEntry.UniqueFileId}). " +
                $"Expected {fileEntry.Size} bytes, actual {properties.ContentLength} bytes.");
        }

        if (!TryGetMetadataValue(properties.Metadata, BackupBlobMetadata.Sha256, out var uploadedSha256))
        {
            throw new InvalidOperationException(
                $"Staged blob is missing required metadata '{BackupBlobMetadata.Sha256}' for '{fileEntry.LogicalPath}' ({fileEntry.UniqueFileId}).");
        }

        if (!string.Equals(uploadedSha256, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
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
