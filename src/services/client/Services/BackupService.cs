using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Telemetry;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace FlorisDeV.BackupClient.Services;

public interface IBackupService
{
    Task<bool> Backup(CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates the backup process by coordinating scanning, hashing, uploading, and state management.
/// </summary>
public partial class BackupService(
    ILogger<BackupService> logger,
    TelemetryProvider telemetry,
    IBackupApiClient backupApiClient,
    IBackupStateService backupStateService,
    IBackupScanner scanner,
    IFileUploader uploader,
    IOptions<BackupClientOptions> backupOptions) : IBackupService
{
    private const int InitialScanProgressFileCount = 1000;
    private static readonly TimeSpan PendingRunSasSafetyWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ScanProgressInterval = TimeSpan.FromMinutes(1);
    private readonly BackupClientOptions _options = backupOptions.Value;

    public async Task<bool> Backup(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var metricTags = new TagList { { "operation.name", "Backup" } };
        using var activity = telemetry.ActivitySource.StartActivity("Backup");

        var targets = _options.GetEffectiveTargets();

        try
        {
            // Step 0: Validate all backup targets before starting
            ValidateTargets(targets);

            // Get or create device state (generates persistent device ID on first run)
            var deviceState = await backupStateService.GetOrCreateDeviceStateAsync(cancellationToken);
            var deviceId = deviceState.DeviceId;

            await RegisterDeviceAsync(deviceId, cancellationToken);
            SetupTelemetryBackupStart(activity, deviceId, targets);

            var pendingRun = await LoadUsablePendingRunAsync(deviceId, cancellationToken);
            var finalizedPendingRun = false;
            if (pendingRun is { ManifestUploaded: true } or { CommitId: not null })
            {
                var finalized = await ResumeFinalizedRunAsync(pendingRun, cancellationToken);
                if (!finalized)
                {
                    stopwatch.Stop();
                    RecordOperationDuration(activity, metricTags, stopwatch, "commit_pending");
                    return true;
                }

                // The invocation that notices a completed pending run must still inspect the live
                // filesystem. Files may have changed while the server was committing (or since the
                // previous timer tick), and returning here would defer them for a whole interval.
                deviceState = await backupStateService.GetOrCreateDeviceStateAsync(cancellationToken);
                pendingRun = null;
                finalizedPendingRun = true;
            }

            if (!finalizedPendingRun)
            {
                // Repairs state written by older clients that promoted every journal entry even when
                // the server rejected some staged blobs. Removing those paths makes this scan retry them.
                await ReconcileLastCommitFailuresAsync(deviceState, cancellationToken);
            }

            // Step 1: Resolve exclusion patterns
            var excludePatterns = ResolveExclusionPatterns();

            // Step 2: Scan, hash, batch, and upload files
            var processingResult = await ProcessFilesAsync(
                deviceState, deviceId, targets, excludePatterns, pendingRun, activity, metricTags, cancellationToken);

            // Step 3: Count deleted files, i.e. tracked files this scan never saw. A SQL anti-join
            // between the recorded scan paths and tracked state, so neither set is materialized in
            // memory. Before the first successful backup there is no tracked state, so this is 0.
            var deletedCount = await backupStateService.CountScanDeletionsAsync(cancellationToken);

            LogDeltaComputed(processingResult.Stats.NewFilesCount, processingResult.Stats.ModifiedFilesCount, deletedCount);
            LogSmartHashingStats(processingResult.Stats.NewFilesCount, processingResult.Stats.ModifiedFilesCount, processingResult.Stats.UnchangedCount, processingResult.Stats.SkippedCount);

            // Step 4: Handle case of no changes
            if (!processingResult.HasStartedBackupRun && deletedCount == 0)
            {
                return HandleNoChanges(activity, metricTags, stopwatch);
            }

            if (!processingResult.HasStartedBackupRun && deletedCount > 0)
            {
                processingResult = await StartDeletionOnlyRunAsync(
                    processingResult,
                    deviceId,
                    activity,
                    metricTags,
                    cancellationToken);
            }

            await backupStateService.RecordScanDeletionsAsync(deviceId, processingResult.RunId!.Value, cancellationToken);

            // Step 5: Commit backup run and update state
            var commitFinalized = await CommitBackupAsync(
                processingResult.HasStartedBackupRun,
                processingResult.RunId,
                deviceId,
                processingResult.ManifestContainerClient,
                processingResult.ManifestBasePath,
                processingResult.ManifestIsPathEmbedded,
                processingResult.UploadSasUrlInfo,
                processingResult.ManifestSasUrlInfo,
                processingResult.StartedAt,
                pendingRun,
                cancellationToken);

            if (!commitFinalized)
            {
                // The commit is still processing server-side and will finalize on a later run. This is a
                // clean, non-fatal outcome: the pending-run journal is intact and no re-upload is needed.
                stopwatch.Stop();
                RecordOperationDuration(activity, metricTags, stopwatch, "commit_pending");
                return true;
            }

            // Step 6: Validate failure threshold and record metrics
            ValidateFailureThreshold(processingResult.Stats.TotalUploadAttempts, processingResult.Stats.TotalUploadFailures);
            ReportSkippedFiles(processingResult.Stats.SkippedCount);
            ReportChangedFiles(processingResult.Stats.SkippedChangedCount);
            ReportSasExpiredFiles(processingResult.Stats.SkippedSasExpiredCount);

            stopwatch.Stop();
            RecordSuccessMetrics(activity, processingResult.RunId, processingResult.Stats, metricTags, stopwatch);

            LogBackupCompleted(processingResult.Stats.NewFilesCount + processingResult.Stats.ModifiedFilesCount, processingResult.Stats.TotalBytes,
                stopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            HandleBackupFailure(ex, activity, stopwatch);
            throw;
        }
    }

    private void ValidateTargets(IReadOnlyDictionary<string, string> targets)
    {
        foreach (var (targetName, targetPath) in targets)
        {
            var validationResult = BackupValidator.ValidateBackupDirectory(targetPath);

            if (validationResult.Severity == ValidationSeverity.Error)
            {
                var ex = new InvalidOperationException(
                    $"Backup target '{targetName}' validation failed: {validationResult.Message}");
                LogBackupValidationFailed(ex);
                throw ex;
            }

            if (validationResult.Severity == ValidationSeverity.Warning)
            {
                LogBackupValidationWarning($"Target '{targetName}': {validationResult.Message}");
            }
        }
    }

    private string[]? ResolveExclusionPatterns()
    {
        var excludePatterns = BackupIgnoreParser.GetCombinedPatterns(_options.IgnoreFilePath);

        if (!string.IsNullOrWhiteSpace(_options.IgnoreFilePath))
        {
            LogUsingIgnoreFile(_options.IgnoreFilePath);
        }

        return excludePatterns;
    }

    private async Task<FileProcessingResult>
        ProcessFilesAsync(
            DeviceState deviceState,
            Guid deviceId,
            IReadOnlyDictionary<string, string> targets,
            string[]? excludePatterns,
            PendingBackupRun? pendingRun,
            Activity? activity,
            TagList metricTags,
            CancellationToken cancellationToken)
    {
        LogScanningDirectories(targets.Count);

        const int batchSize = 100;
        const long batchSizeBytes = 100 * 1024 * 1024; // 100 MB

        // Deletion detection compares tracked state against the paths this scan saw. They are
        // flushed to a scratch table in blocks so the scanned set never accumulates in memory.
        const int scannedPathFlushSize = 1000;

        await backupStateService.BeginScanAsync(cancellationToken);

        var stats = new BackupStats();
        var currentBatch = new List<TaggedFile>();
        var scannedPaths = new List<string>(scannedPathFlushSize);
        var scanStopwatch = Stopwatch.StartNew();
        var lastProgressElapsed = TimeSpan.Zero;
        long currentBatchBytes = 0;

        var backupType = deviceState.LastSuccessfulBackup == null ? BackupType.Full : BackupType.Incremental;
        var hasStartedBackupRun = false;
        Guid? runId = null;
        BlobContainerClient? containerClient = null;
        BlobContainerClient? manifestContainerClient = null;
        string? manifestBasePath = null;
        bool manifestIsPathEmbedded = false;
        var startedAt = DateTimeOffset.UtcNow;
        var uploadSasUrlInfo = pendingRun?.UploadSasUrlInfo;
        var manifestSasUrlInfo = pendingRun?.ManifestSasUrlInfo;

        if (pendingRun != null)
        {
            runId = pendingRun.RunId;
            containerClient = new BlobContainerClient(TranslateStorageUrlForLocalDevelopment(pendingRun.UploadSasUrlInfo.Url));
            manifestContainerClient = new BlobContainerClient(TranslateStorageUrlForLocalDevelopment(pendingRun.ManifestSasUrlInfo.Url));
            manifestBasePath = pendingRun.ManifestSasUrlInfo.BasePath;
            manifestIsPathEmbedded = pendingRun.ManifestSasUrlInfo.IsPathEmbedded;
            startedAt = pendingRun.StartedAt;
            hasStartedBackupRun = true;
            uploader.SetBasePath(pendingRun.UploadSasUrlInfo.BasePath, pendingRun.UploadSasUrlInfo.IsPathEmbedded);
            LogPendingBackupRunResumed(pendingRun.RunId, pendingRun.UploadedFileCount);
        }

        await foreach (var taggedFile in scanner.ScanAllTargetsAsync(targets, excludePatterns, cancellationToken))
        {
            stats.TotalScanned++;

            scannedPaths.Add(taggedFile.GetStoragePath());
            if (scannedPaths.Count >= scannedPathFlushSize)
            {
                await backupStateService.AppendScannedPathsAsync(scannedPaths, cancellationToken);
                scannedPaths.Clear();
            }

            var (fileWithHash, needsBackup, changeType) = await scanner.AnalyzeFileAsync(taggedFile, cancellationToken);

            switch (changeType)
            {
                case FileChangeType.New:
                    stats.NewFilesCount++;
                    break;
                case FileChangeType.Modified:
                    stats.ModifiedFilesCount++;
                    break;
                case FileChangeType.Unchanged:
                    stats.UnchangedCount++;
                    break;
                case FileChangeType.Skipped:
                    stats.SkippedCount++;
                    break;
            }

            var scanElapsed = scanStopwatch.Elapsed;
            if (ShouldLogScanProgress(stats.TotalScanned, scanElapsed, lastProgressElapsed))
            {
                LogScanProgress(stats.TotalScanned,
                    stats.NewFilesCount + stats.ModifiedFilesCount,
                    stats.UnchangedCount,
                    stats.SkippedCount,
                    taggedFile.TargetName,
                    scanElapsed.TotalMinutes,
                    stats.TotalScanned / Math.Max(scanElapsed.TotalSeconds, 0.001));
                lastProgressElapsed = scanElapsed;
            }

            if (changeType == FileChangeType.Skipped)
            {
                continue; // Skip this file entirely
            }

            if (needsBackup)
            {
                // On resume, a blob already staged for this run under an unchanged hash and size is
                // skipped. The match is a single indexed lookup against the run's journal, so the
                // set of already-uploaded files stays on disk.
                var resumedFile = pendingRun == null
                    ? null
                    : await backupStateService.FindStagedRunFileAsync(
                        deviceId,
                        pendingRun.RunId,
                        fileWithHash.GetStoragePath(),
                        fileWithHash.Metadata.Hash,
                        fileWithHash.Metadata.SizeBytes,
                        cancellationToken);

                var stagedFile = resumedFile ?? fileWithHash with
                {
                    UniqueFileId = GenerateUniqueFileId(fileWithHash.Metadata.Hash)
                };

                stats.TotalBytes += stagedFile.Metadata.SizeBytes;

                if (resumedFile == null)
                {
                    currentBatch.Add(stagedFile);
                    currentBatchBytes += stagedFile.Metadata.SizeBytes;
                }

                // A resumed file is already staged and already in the run's journal, so there is
                // nothing to upload and nothing to record for it.
            }

            if (currentBatch.Count >= batchSize || currentBatchBytes >= batchSizeBytes)
            {
                var batchResult = await ProcessBatchAsync(
                    hasStartedBackupRun, runId, containerClient, manifestContainerClient, manifestBasePath, manifestIsPathEmbedded, deviceId, backupType,
                    currentBatch, currentBatchBytes, stats, activity, metricTags, startedAt, uploadSasUrlInfo, manifestSasUrlInfo, cancellationToken);

                hasStartedBackupRun = batchResult.HasStartedBackupRun;
                runId = batchResult.RunId;
                containerClient = batchResult.ContainerClient;
                manifestContainerClient = batchResult.ManifestContainerClient;
                manifestBasePath = batchResult.ManifestBasePath;
                manifestIsPathEmbedded = batchResult.ManifestIsPathEmbedded;
                startedAt = batchResult.StartedAt;
                uploadSasUrlInfo = batchResult.UploadSasUrlInfo;
                manifestSasUrlInfo = batchResult.ManifestSasUrlInfo;

                currentBatch.Clear();
                currentBatchBytes = 0;
            }
        }

        if (scannedPaths.Count > 0)
        {
            await backupStateService.AppendScannedPathsAsync(scannedPaths, cancellationToken);
            scannedPaths.Clear();
        }

        LogScannedFiles(stats.TotalScanned);

        if (currentBatch.Count > 0)
        {
            var batchResult = await ProcessBatchAsync(
                hasStartedBackupRun, runId, containerClient, manifestContainerClient, manifestBasePath, manifestIsPathEmbedded, deviceId, backupType,
                currentBatch, currentBatchBytes, stats, activity, metricTags, startedAt, uploadSasUrlInfo, manifestSasUrlInfo, cancellationToken);

            hasStartedBackupRun = batchResult.HasStartedBackupRun;
            runId = batchResult.RunId;
            containerClient = batchResult.ContainerClient;
            manifestContainerClient = batchResult.ManifestContainerClient;
            manifestBasePath = batchResult.ManifestBasePath;
            manifestIsPathEmbedded = batchResult.ManifestIsPathEmbedded;
            startedAt = batchResult.StartedAt;
            uploadSasUrlInfo = batchResult.UploadSasUrlInfo;
            manifestSasUrlInfo = batchResult.ManifestSasUrlInfo;
        }

        return new FileProcessingResult(
            stats,
            hasStartedBackupRun,
            runId,
            manifestContainerClient,
            manifestBasePath,
            manifestIsPathEmbedded,
            uploadSasUrlInfo,
            manifestSasUrlInfo,
            startedAt);
    }

    internal static bool ShouldLogScanProgress(
        int totalScanned,
        TimeSpan elapsed,
        TimeSpan lastProgressElapsed)
        => totalScanned == InitialScanProgressFileCount ||
           elapsed - lastProgressElapsed >= ScanProgressInterval;

    private async Task<BatchProcessingResult> ProcessBatchAsync(
        bool hasStartedBackupRun,
        Guid? runId,
        BlobContainerClient? containerClient,
        BlobContainerClient? manifestContainerClient,
        string? manifestBasePath,
        bool manifestIsPathEmbedded,
        Guid deviceId,
        BackupType backupType,
        List<TaggedFile> currentBatch,
        long currentBatchBytes,
        BackupStats stats,
        Activity? activity,
        TagList metricTags,
        DateTimeOffset startedAt,
        SasUrlInfo? uploadSasUrlInfo,
        SasUrlInfo? manifestSasUrlInfo,
        CancellationToken cancellationToken)
    {
        if (!hasStartedBackupRun)
        {
            // Start backup run on first batch with changes
            var runStart = await StartBackupRunAsync(
                deviceId,
                backupType,
                currentBatch.Count,
                currentBatchBytes,
                activity,
                metricTags,
                cancellationToken);

            runId = runStart.RunId;
            containerClient = runStart.ContainerClient;
            manifestContainerClient = runStart.ManifestContainerClient;
            manifestBasePath = runStart.ManifestBasePath;
            manifestIsPathEmbedded = runStart.ManifestIsPathEmbedded;
            startedAt = runStart.StartedAt;
            uploadSasUrlInfo = runStart.UploadSasUrlInfo;
            manifestSasUrlInfo = runStart.ManifestSasUrlInfo;
            hasStartedBackupRun = true;

            await SavePendingRunAsync(deviceId, runId.Value, startedAt, uploadSasUrlInfo, manifestSasUrlInfo, false, null, cancellationToken);
        }

        // Drop files whose on-disk size/mtime changed since they were scanned and hashed: their
        // staged bytes would no longer match the manifest and the server would reject them. They are
        // left un-backed-up locally, so the next run re-detects and uploads them. A residual race
        // (change after this check but before the read) is caught server-side.
        var (stableBatch, changedFiles) = FilterFilesChangedSinceScan(currentBatch);
        foreach (var changed in changedFiles)
        {
            stats.SkippedChangedCount++;
            LogFileChangedDuringBackup(changed.Metadata.FilePath, changed.Metadata.SizeBytes, GetCurrentFileSize(changed.Metadata.FilePath));
        }

        // Ensure the run's SAS token can outlast this batch; refresh it proactively if it is close to
        // expiry. A single SAS is minted for the whole run, so a long backup will otherwise cross the
        // token's window and every remaining upload fails with AuthenticationFailed.
        var sas = await EnsureSasFreshForBatchAsync(
            deviceId, runId!.Value, currentBatchBytes,
            new SasContext(containerClient!, manifestContainerClient!, uploadSasUrlInfo!, manifestSasUrlInfo!, manifestBasePath, manifestIsPathEmbedded),
            forceRefresh: false, cancellationToken);
        (containerClient, manifestContainerClient, uploadSasUrlInfo, manifestSasUrlInfo, manifestBasePath, manifestIsPathEmbedded) =
            (sas.ContainerClient, sas.ManifestContainerClient, sas.UploadSasUrlInfo, sas.ManifestSasUrlInfo, sas.ManifestBasePath, sas.ManifestIsPathEmbedded);

        // Upload batch
        stats.TotalUploadAttempts += stableBatch.Count;
        var uploadResult = await uploader.UploadFilesAsync(containerClient!, stableBatch, cancellationToken);
        var uploadedFiles = new List<TaggedFile>(uploadResult.Uploaded);
        var otherFailureCount = uploadResult.OtherFailureCount;

        // If the token expired partway through the batch, refresh it and retry only the affected files.
        if (uploadResult.SasExpiredFiles.Count > 0)
        {
            LogSasExpiredMidBatch(uploadResult.SasExpiredFiles.Count, runId.Value);
            sas = await EnsureSasFreshForBatchAsync(deviceId, runId.Value, currentBatchBytes, sas, forceRefresh: true, cancellationToken);
            (containerClient, manifestContainerClient, uploadSasUrlInfo, manifestSasUrlInfo, manifestBasePath, manifestIsPathEmbedded) =
                (sas.ContainerClient, sas.ManifestContainerClient, sas.UploadSasUrlInfo, sas.ManifestSasUrlInfo, sas.ManifestBasePath, sas.ManifestIsPathEmbedded);

            var retryResult = await uploader.UploadFilesAsync(containerClient!, uploadResult.SasExpiredFiles, cancellationToken);
            uploadedFiles.AddRange(retryResult.Uploaded);
            otherFailureCount += retryResult.OtherFailureCount;

            var stillExpired = retryResult.SasExpiredFiles.Count;
            if (stillExpired > 0)
            {
                // A freshly issued token still expired before these files finished (pathological: a
                // single batch slower than a whole token lifetime). Defer them to the next run instead
                // of counting them toward the failure threshold and aborting an otherwise healthy backup.
                stats.SkippedSasExpiredCount += stillExpired;
                LogSasExpiredUnrecovered(stillExpired, runId.Value);
            }
        }

        var failedCount = otherFailureCount;
        stats.TotalUploadFailures += failedCount;

        // Check failure threshold after each batch (early detection). SAS-expiry files are intentionally
        // excluded from the failure count above: they are recoverable and re-detected on the next run.
        CheckFailureThresholdProgress(stats.TotalUploadAttempts, stats.TotalUploadFailures);

        // Local file state is updated only after the server-side commit succeeds. The journal is
        // appended to rather than rewritten, so the cost of recording a batch is proportional to the
        // batch, not to everything uploaded so far.
        await backupStateService.AppendPendingRunFilesAsync(deviceId, runId!.Value, uploadedFiles, cancellationToken);
        await SavePendingRunAsync(deviceId, runId.Value, startedAt, uploadSasUrlInfo!, manifestSasUrlInfo!, false, null, cancellationToken);
        LogBatchProcessed(uploadedFiles.Count, currentBatchBytes, stats.TotalScanned);

        if (failedCount > 0)
        {
            LogBatchPartialFailure(failedCount, currentBatch.Count);
        }

        return new BatchProcessingResult(
            hasStartedBackupRun,
            runId,
            containerClient,
            manifestContainerClient,
            manifestBasePath,
                manifestIsPathEmbedded,
                startedAt,
                uploadSasUrlInfo,
                manifestSasUrlInfo);
    }

    private async Task<FileProcessingResult> StartDeletionOnlyRunAsync(
        FileProcessingResult processingResult,
        Guid deviceId,
        Activity? activity,
        TagList metricTags,
        CancellationToken cancellationToken)
    {
        var runStart = await StartBackupRunAsync(
            deviceId,
            BackupType.Incremental,
            batchFileCount: 0,
            batchSizeBytes: 0,
            activity,
            metricTags,
            cancellationToken);

        return processingResult with
        {
            HasStartedBackupRun = true,
            RunId = runStart.RunId,
            ManifestContainerClient = runStart.ManifestContainerClient,
            ManifestBasePath = runStart.ManifestBasePath,
            ManifestIsPathEmbedded = runStart.ManifestIsPathEmbedded,
            UploadSasUrlInfo = runStart.UploadSasUrlInfo,
            ManifestSasUrlInfo = runStart.ManifestSasUrlInfo,
            StartedAt = runStart.StartedAt
        };
    }

    private async Task<RunStartResult> StartBackupRunAsync(
        Guid deviceId,
        BackupType backupType,
        int batchFileCount,
        long batchSizeBytes,
        Activity? activity,
        TagList metricTags,
        CancellationToken cancellationToken)
    {
        var startResponse = await backupApiClient.StartBackupRun(deviceId, cancellationToken);

        var sasUrl = TranslateStorageUrlForLocalDevelopment(startResponse.SasUrlInfo.Url);
        var containerClient = new BlobContainerClient(sasUrl);

        uploader.SetBasePath(startResponse.SasUrlInfo.BasePath, startResponse.SasUrlInfo.IsPathEmbedded);

        var manifestSasInfo = startResponse.ManifestSasUrlInfo ?? startResponse.SasUrlInfo;
        var manifestSasUrl = TranslateStorageUrlForLocalDevelopment(manifestSasInfo.Url);
        var manifestContainerClient = new BlobContainerClient(manifestSasUrl);

        var backupTypeString = backupType.ToString().ToLowerInvariant();
        activity?.SetTag(ActivityAttributes.BackupType, backupTypeString);
        metricTags.Add("backup.type", backupTypeString);

        LogBackupRunStarted(startResponse.RunId, batchFileCount, batchSizeBytes);

        return new RunStartResult(
            startResponse.RunId,
            containerClient,
            manifestContainerClient,
            manifestSasInfo.BasePath,
            manifestSasInfo.IsPathEmbedded,
            startResponse.StartedAt,
            startResponse.SasUrlInfo,
            manifestSasInfo);
    }

    private async Task RegisterDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var response = await backupApiClient.RegisterDevice(new RegisterDeviceRequest
        {
            DeviceId = deviceId,
            DisplayName = Environment.MachineName
        }, cancellationToken);

        if (response.DeviceId != deviceId)
        {
            throw new InvalidOperationException($"Registered device ID {response.DeviceId} did not match local device ID {deviceId}");
        }
    }


    private bool HandleNoChanges(Activity? activity, TagList metricTags, Stopwatch stopwatch)
    {
        LogNoChangesDetected();
        stopwatch.Stop();

        RecordOperationDuration(activity, metricTags, stopwatch, "no_changes");

        activity?.SetTag(ActivityAttributes.OperationStatus, "skipped");
        activity?.SetTag(ActivityAttributes.BackupSuccess, true);
        activity?.SetTag("backup.skipped", true);

        return true;
    }

    /// <summary>
    /// Uploads the manifest, commits the run, and waits for server-side completion. Returns <c>true</c>
    /// when the run was finalized locally; <c>false</c> when the commit is still processing server-side
    /// and finalization was deferred to a later run (the pending-run journal is left in place).
    /// </summary>
    private async Task<bool> CommitBackupAsync(
        bool hasStartedBackupRun,
        Guid? runId,
        Guid deviceId,
        BlobContainerClient? manifestContainerClient,
        string? manifestBasePath,
        bool manifestIsPathEmbedded,
        SasUrlInfo? uploadSasUrlInfo,
        SasUrlInfo? manifestSasUrlInfo,
        DateTimeOffset startedAt,
        PendingBackupRun? pendingRun,
        CancellationToken cancellationToken)
    {
        if (!hasStartedBackupRun)
            return true;

        if (manifestContainerClient == null)
            throw new InvalidOperationException("Manifest upload client was not initialized for the backup run");

        if (runId == null)
            throw new InvalidOperationException("Backup run ID was not initialized for the backup run");

        if (pendingRun?.ManifestUploaded != true)
        {
            // Entries are read out of the journal and written to the blob as they arrive, so the
            // manifest is never assembled as a document in memory.
            await uploader.UploadRunManifestAsync(
                manifestContainerClient,
                deviceId,
                runId.Value,
                StreamManifestEntriesAsync(deviceId, runId.Value, cancellationToken),
                backupStateService.StreamPendingRunDeletionsAsync(deviceId, runId.Value, cancellationToken),
                manifestBasePath,
                manifestIsPathEmbedded,
                cancellationToken);

            await SavePendingRunAsync(deviceId, runId.Value, startedAt, uploadSasUrlInfo!, manifestSasUrlInfo!, true, null, cancellationToken);
        }

        var commitId = pendingRun?.CommitId;
        if (commitId == null)
        {
            var commitRequest = new CommitBackupRunRequest
            {
                RunId = runId.Value
            };
            var commitResponse = await backupApiClient.CommitBackupRun(deviceId, commitRequest, cancellationToken);
            commitId = commitResponse.CommitId;

            await SavePendingRunAsync(deviceId, runId.Value, startedAt, uploadSasUrlInfo!, manifestSasUrlInfo!, true, commitId, cancellationToken);
        }

        LogBackupRunCommitted(runId.Value);

        var commitStatus = await WaitForCommitCompletionAsync(deviceId, commitId.Value, cancellationToken);
        if (commitStatus == null)
        {
            // Commit still processing server-side; leave the pending run so a later run finalizes it.
            return false;
        }

        await FinalizeRunStateAsync(deviceId, runId.Value, commitStatus, cancellationToken);
        return true;
    }

    /// <summary>
    /// Promotes a committed run's journal into tracked file state, applies its deletions, and clears
    /// the journal. Every step is set-based inside SQLite, so finalizing a run with hundreds of
    /// thousands of files costs no managed memory.
    /// </summary>
    private async Task FinalizeRunStateAsync(
        Guid deviceId,
        Guid runId,
        CommitStatusResponse commitStatus,
        CancellationToken cancellationToken)
    {
        if (commitStatus.Status == CommitJobStatus.CompletedWithErrors)
        {
            await RemoveServerRejectedFilesFromJournalAsync(
                deviceId,
                runId,
                commitStatus.CommitId,
                commitStatus.FilesFailed ?? 0,
                cancellationToken);
        }

        await backupStateService.PromotePendingRunFilesToStateAsync(deviceId, runId, cancellationToken);
        await backupStateService.ApplyPendingRunDeletionsAsync(deviceId, runId, cancellationToken);

        await backupStateService.SaveBackupSuccessAsync(
            runId,
            commitStatus.CommitId.ToString("N"),
            [],
            cancellationToken);

        await backupStateService.ClearPendingBackupRunAsync(deviceId, runId, cancellationToken);
    }

    private async Task RemoveServerRejectedFilesFromJournalAsync(
        Guid deviceId,
        Guid runId,
        Guid commitId,
        int expectedFailedFiles,
        CancellationToken cancellationToken)
        => await ProcessServerRejectedFilesAsync(
            deviceId,
            runId,
            commitId,
            expectedFailedFiles,
            (paths, token) => backupStateService.RemovePendingRunFilesAsync(deviceId, runId, paths, token),
            cancellationToken);

    private async Task ReconcileLastCommitFailuresAsync(
        DeviceState deviceState,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(deviceState.LastCommitId, out var commitId) || deviceState.LastRunId == null)
        {
            return;
        }

        CommitStatusResponse commitStatus;
        try
        {
            commitStatus = await backupApiClient.GetCommitStatus(deviceState.DeviceId, commitId, cancellationToken);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Commit progress may have aged out under a future retention policy. That must not block
            // ordinary backups; there is simply no server-side failure detail left to reconcile.
            return;
        }

        if (commitStatus.Status != CommitJobStatus.CompletedWithErrors)
        {
            return;
        }

        await ProcessServerRejectedFilesAsync(
            deviceState.DeviceId,
            deviceState.LastRunId.Value,
            commitId,
            commitStatus.FilesFailed ?? 0,
            (paths, token) => backupStateService.RemoveTrackedFilesAsync(paths, token),
            cancellationToken);
    }

    private async Task ProcessServerRejectedFilesAsync(
        Guid deviceId,
        Guid runId,
        Guid commitId,
        int expectedFailedFiles,
        Func<IReadOnlyList<string>, CancellationToken, Task> removeFiles,
        CancellationToken cancellationToken)
    {
        string? continuationToken = null;
        var failedFilesRead = 0;

        do
        {
            var page = await backupApiClient.ListFailedCommitFiles(
                deviceId,
                commitId,
                pageSize: 500,
                continuationToken,
                cancellationToken);

            if (page.DeviceId != deviceId || page.RunId != runId || page.CommitId != commitId)
            {
                throw new InvalidOperationException($"Failed-file results did not match commit {commitId}");
            }

            if (page.Files.Any(file => file.Status != CommitFileStatus.Failed))
            {
                throw new InvalidOperationException($"Failed-file results for commit {commitId} contained a non-failed entry");
            }

            await removeFiles(
                page.Files.Select(file => file.LogicalPath).ToArray(),
                cancellationToken);

            failedFilesRead += page.Files.Count;
            continuationToken = page.NextContinuationToken;
        } while (!string.IsNullOrWhiteSpace(continuationToken));

        // Do not promote the journal on a stale/partial read. The pending run remains durable and a
        // later timer invocation can retry once every terminal progress record is visible.
        if (failedFilesRead != expectedFailedFiles)
        {
            throw new InvalidOperationException(
                $"Commit {commitId} reported {expectedFailedFiles} failed file(s), but the failed-file endpoint returned {failedFilesRead}");
        }
    }

    /// <summary>
    /// Projects the run's journal into manifest entries one at a time.
    /// </summary>
    private async IAsyncEnumerable<ManifestFileEntry> StreamManifestEntriesAsync(
        Guid deviceId,
        Guid runId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var file in backupStateService.StreamPendingRunFilesAsync(deviceId, runId, cancellationToken))
        {
            yield return new ManifestFileEntry
            {
                TargetName = file.TargetName,
                RelativePath = file.GetRelativePath(),
                UniqueFileId = file.UniqueFileId
                    ?? throw new InvalidOperationException($"Missing unique file ID for {file.GetStoragePath()}"),
                Sha256 = file.GetUploadSha256(),
                Size = file.GetUploadSizeBytes(),
                Mtime = file.Metadata.LastModified,
                Encryption = file.Encryption
            };
        }
    }

    private async Task<PendingBackupRun?> LoadUsablePendingRunAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var pendingRun = await backupStateService.GetPendingBackupRunAsync(deviceId, cancellationToken);
        if (pendingRun == null)
        {
            return null;
        }

        if (pendingRun.HasUsableSas(DateTimeOffset.UtcNow, PendingRunSasSafetyWindow) ||
            pendingRun.ManifestUploaded || pendingRun.CommitId.HasValue)
        {
            return pendingRun;
        }

        await backupStateService.ClearPendingBackupRunAsync(deviceId, pendingRun.RunId, cancellationToken);
        LogPendingBackupRunExpired(pendingRun.RunId, pendingRun.ExpiresAt);
        return null;
    }

    private async Task<bool> ResumeFinalizedRunAsync(
        PendingBackupRun pendingRun,
        CancellationToken cancellationToken)
    {
        var commitId = pendingRun.CommitId;
        if (commitId == null)
        {
            var commitResponse = await backupApiClient.CommitBackupRun(pendingRun.DeviceId, new CommitBackupRunRequest
            {
                RunId = pendingRun.RunId
            }, cancellationToken);
            commitId = commitResponse.CommitId;

            await backupStateService.SavePendingBackupRunAsync(pendingRun with
            {
                ManifestUploaded = true,
                CommitId = commitId
            }, cancellationToken);
        }

        var commitStatus = await WaitForCommitCompletionAsync(pendingRun.DeviceId, commitId.Value, cancellationToken);
        if (commitStatus == null)
        {
            // Still processing server-side; keep the pending run so a later run finalizes it.
            return false;
        }

        await FinalizeRunStateAsync(pendingRun.DeviceId, pendingRun.RunId, commitStatus, cancellationToken);
        LogPendingBackupRunFinalized(pendingRun.RunId, commitId.Value);
        return true;
    }

    private Task SavePendingRunAsync(
        Guid deviceId,
        Guid runId,
        DateTimeOffset startedAt,
        SasUrlInfo uploadSasUrlInfo,
        SasUrlInfo manifestSasUrlInfo,
        bool manifestUploaded,
        Guid? commitId,
        CancellationToken cancellationToken)
        => backupStateService.SavePendingBackupRunAsync(new PendingBackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = startedAt,
            UploadSasUrlInfo = uploadSasUrlInfo,
            ManifestSasUrlInfo = manifestSasUrlInfo,
            ManifestUploaded = manifestUploaded,
            CommitId = commitId
        }, cancellationToken);

    /// <summary>
    /// Polls the server-side commit until it reaches a terminal state.
    /// Returns the terminal response when the commit completed (<see cref="CommitJobStatus.Succeeded"/> or
    /// <see cref="CommitJobStatus.CompletedWithErrors"/>), and <c>null</c> when the commit is still
    /// processing after the configured wait. Large runs
    /// can take far longer server-side than the client wants to block, so the caller leaves the pending
    /// run in place and a later run finalizes it. Throws only on a genuine commit failure.
    /// </summary>
    private async Task<CommitStatusResponse?> WaitForCommitCompletionAsync(
        Guid deviceId,
        Guid commitId,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.CommitStatusTimeoutSeconds);
        var pollInterval = TimeSpan.FromSeconds(_options.CommitStatusPollIntervalSeconds);
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await backupApiClient.GetCommitStatus(deviceId, commitId, cancellationToken);

            switch (status.Status)
            {
                case CommitJobStatus.Succeeded:
                    return status;
                case CommitJobStatus.CompletedWithErrors:
                    // Non-fatal: the server committed the run but skipped some files whose staged content
                    // did not match. Those files stay un-backed-up locally and are retried on the next run.
                    LogCommitCompletedWithErrors(status.FilesFailed ?? 0, status.Error ?? string.Empty);
                    return status;
                case CommitJobStatus.Failed:
                    throw new InvalidOperationException($"Backup commit {commitId} failed: {status.Error ?? "Unknown error"}");
            }

            if (stopwatch.Elapsed >= timeout)
            {
                // Still Queued/Processing. Don't fail - the commit is durable server-side and will finish;
                // hand off finalization to a later run, which resumes from the pending-run journal.
                LogCommitStillProcessing(commitId, status.Status.ToString(), timeout.TotalSeconds);
                return null;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    private static string GenerateUniqueFileId(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new InvalidOperationException("Cannot generate a unique file ID without a SHA-256 hash");
        }

        Span<byte> randomBytes = stackalloc byte[5];
        RandomNumberGenerator.Fill(randomBytes);
        var suffix = Convert.ToHexString(randomBytes).ToLowerInvariant();

        return $"{sha256}_{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}_{suffix}";
    }

    private void ValidateFailureThreshold(int totalUploadAttempts, int totalUploadFailures)
    {
        if (totalUploadAttempts == 0)
            return;

        var failurePercentage = (totalUploadFailures * 100.0) / totalUploadAttempts;

        if (failurePercentage > _options.MaxFailurePercentage)
        {
            var errorMessage = $"Backup failed: {totalUploadFailures}/{totalUploadAttempts} files failed " +
                               $"({failurePercentage:F1}%), exceeding {_options.MaxFailurePercentage}% threshold";
            LogBackupFailureThresholdExceeded(totalUploadFailures, totalUploadAttempts, failurePercentage,
                _options.MaxFailurePercentage);
            throw new InvalidOperationException(errorMessage);
        }

        if (totalUploadFailures > 0)
        {
            LogBackupCompletedWithFailures(totalUploadAttempts - totalUploadFailures, totalUploadFailures,
                failurePercentage);
        }
    }

    /// <summary>
    /// Re-issues the run's upload/manifest SAS tokens when the current one cannot safely outlast the
    /// batch about to be uploaded (or when a mid-batch expiry forces it). Rebuilds the container clients
    /// bound to the fresh tokens and keeps the uploader's base path in sync.
    /// </summary>
    private async Task<SasContext> EnsureSasFreshForBatchAsync(
        Guid deviceId,
        Guid runId,
        long batchBytes,
        SasContext current,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh && SasWillOutlastBatch(current.UploadSasUrlInfo, batchBytes))
        {
            return current;
        }

        LogRefreshingUploadSas(runId, current.UploadSasUrlInfo.ExpiresAt);

        var refreshed = await backupApiClient.RefreshBackupRunSas(deviceId, runId, cancellationToken);

        var containerClient = new BlobContainerClient(TranslateStorageUrlForLocalDevelopment(refreshed.SasUrlInfo.Url));
        var manifestSas = refreshed.ManifestSasUrlInfo ?? refreshed.SasUrlInfo;
        var manifestContainerClient = new BlobContainerClient(TranslateStorageUrlForLocalDevelopment(manifestSas.Url));

        // The uploader prepends BasePath to each blob name; keep it aligned with the new token.
        uploader.SetBasePath(refreshed.SasUrlInfo.BasePath, refreshed.SasUrlInfo.IsPathEmbedded);

        LogRefreshedUploadSas(runId, refreshed.SasUrlInfo.ExpiresAt);
        return new SasContext(
            containerClient,
            manifestContainerClient,
            refreshed.SasUrlInfo,
            manifestSas,
            manifestSas.BasePath,
            manifestSas.IsPathEmbedded);
    }

    /// <summary>
    /// Returns true when the SAS token's remaining lifetime comfortably exceeds the estimated time to
    /// upload <paramref name="batchBytes"/>, leaving a safety margin. Uses the same throughput
    /// assumption as the large-file timeout warning.
    /// </summary>
    private static bool SasWillOutlastBatch(SasUrlInfo sas, long batchBytes)
    {
        const long assumedBytesPerSecond = 10L * 1024 * 1024; // 10 MB/s
        var estimatedBatchDuration = TimeSpan.FromSeconds((double)batchBytes / assumedBytesPerSecond);
        var remaining = sas.ExpiresAt - DateTimeOffset.UtcNow;
        return remaining > estimatedBatchDuration + PendingRunSasSafetyWindow;
    }

    private void ReportSasExpiredFiles(int sasExpiredCount)
    {
        if (sasExpiredCount > 0)
        {
            LogBackupCompletedWithSasExpiredFiles(sasExpiredCount);
        }
    }

    private void ReportSkippedFiles(int skippedCount)
    {
        if (skippedCount > 0)
        {
            LogBackupCompletedWithSkippedFiles(skippedCount, _options.LockedFilePolicy.ToString());
        }
    }

    private void ReportChangedFiles(int changedCount)
    {
        if (changedCount > 0)
        {
            LogBackupCompletedWithChangedFiles(changedCount);
        }
    }

    /// <summary>
    /// Partitions a batch into files that still match their scanned size/mtime and files that have
    /// changed on disk since scanning (or vanished). Changed files must not be uploaded: their staged
    /// bytes would no longer match the size/hash recorded in the manifest.
    /// </summary>
    private static (List<TaggedFile> Stable, List<TaggedFile> Changed) FilterFilesChangedSinceScan(
        IReadOnlyList<TaggedFile> batch)
    {
        var stable = new List<TaggedFile>(batch.Count);
        var changed = new List<TaggedFile>();

        foreach (var file in batch)
        {
            var info = new FileInfo(file.Metadata.FilePath);

            if (info.Exists
                && (info.Length != file.Metadata.SizeBytes
                    || info.LastWriteTimeUtc != file.Metadata.LastModified.UtcDateTime))
            {
                changed.Add(file);
            }
            else
            {
                stable.Add(file);
            }
        }

        return (stable, changed);
    }

    private static long GetCurrentFileSize(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : -1;
    }

    /// <summary>
    /// Checks failure threshold during batch processing for early detection.
    /// Throws if threshold exceeded, warns if approaching threshold.
    /// </summary>
    private void CheckFailureThresholdProgress(int totalUploadAttempts, int totalUploadFailures)
    {
        if (totalUploadAttempts == 0)
            return;

        var failurePercentage = (totalUploadFailures * 100.0) / totalUploadAttempts;

        // Fail fast if threshold already exceeded
        if (failurePercentage > _options.MaxFailurePercentage)
        {
            var errorMessage = $"Backup aborted: {totalUploadFailures}/{totalUploadAttempts} files failed " +
                               $"({failurePercentage:F1}%), exceeding {_options.MaxFailurePercentage}% threshold";
            LogBackupFailureThresholdExceeded(totalUploadFailures, totalUploadAttempts, failurePercentage,
                _options.MaxFailurePercentage);
            throw new InvalidOperationException(errorMessage);
        }

        // Warn if approaching threshold (>50% of max)
        var warningThreshold = _options.MaxFailurePercentage * 0.5;
        if (failurePercentage > warningThreshold && totalUploadAttempts >= 10)
        {
            LogBackupFailureWarning(totalUploadFailures, totalUploadAttempts, failurePercentage,
                _options.MaxFailurePercentage);
        }
    }

    private void SetupTelemetryBackupStart(Activity? activity, Guid deviceId, IReadOnlyDictionary<string, string> targets)
    {
        activity?.SetTag(ActivityAttributes.OperationName, "Backup");
        activity?.SetTag("device.id", deviceId);
        activity?.SetTag("backup.targets", string.Join(", ", targets.Keys));

        if (logger.IsEnabled(LogLevel.Information))
#pragma warning disable CA1873
            LogBackupStarted(deviceId, string.Join(", ", targets.Select(t => $"{t.Key}={t.Value}")));
#pragma warning restore CA1873
    }

    private void RecordSuccessMetrics(Activity? activity, Guid? runId, BackupStats stats, TagList metricTags,
        Stopwatch stopwatch)
    {
        metricTags.Add("backup.status", "success");

        var totalChangedFiles = stats.NewFilesCount + stats.ModifiedFilesCount;
        telemetry.CountFiles.Add(totalChangedFiles, metricTags);
        telemetry.BackupDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);
        telemetry.BackupSize.Record(stats.TotalBytes, metricTags);

        activity?.SetTag(ActivityAttributes.OperationStatus, "success");
        activity?.SetTag(ActivityAttributes.BackupSuccess, true);
        if (runId.HasValue)
        {
            activity?.SetTag("backup.run_id", runId.Value);
        }

        activity?.SetTag("backup.files.new", stats.NewFilesCount);
        activity?.SetTag("backup.files.modified", stats.ModifiedFilesCount);
        activity?.SetTag("backup.files.deleted", 0); // Deleted files tracked separately
        activity?.SetTag("backup.files.unchanged", stats.UnchangedCount);
        activity?.SetTag("backup.files.skipped", stats.SkippedCount);
        if (stats.SkippedCount > 0)
        {
            activity?.SetTag("backup.degraded", true);
            activity?.SetTag("backup.degraded_reason", "skipped_files");
            activity?.AddEvent(new ActivityEvent("backup.completed_with_skipped_files", tags: new ActivityTagsCollection
            {
                { "skipped_count", stats.SkippedCount },
                { "locked_file_policy", _options.LockedFilePolicy.ToString() }
            }));
        }
    }

    private void RecordOperationDuration(Activity? activity, TagList metricTags, Stopwatch stopwatch, string status)
    {
        var tags = new TagList();
        foreach (var tag in metricTags)
        {
            tags.Add(tag);
        }

        tags.Add("backup.status", status);
        telemetry.BackupDuration.Record(stopwatch.ElapsedMilliseconds, tags);

        activity?.SetTag("backup.duration_ms", stopwatch.ElapsedMilliseconds);
    }

    private void HandleBackupFailure(Exception ex, Activity? activity, Stopwatch stopwatch)
    {
        stopwatch.Stop();

        var failureTags = new TagList
        {
            { "operation.name", "Backup" },
            { "backup.status", "failure" },
            { "error.type", ex.GetType().Name }
        };
        telemetry.CountBackupFailures.Add(1, failureTags);
        telemetry.BackupDuration.Record(stopwatch.ElapsedMilliseconds, failureTags);

        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.SetTag(ActivityAttributes.OperationStatus, "error");
        activity?.SetTag(ActivityAttributes.BackupSuccess, false);
        activity?.SetTag(ActivityAttributes.ErrorType, ex.GetType().Name);
        activity?.SetTag(ActivityAttributes.ErrorMessage, ex.Message);
        activity?.AddException(ex);

        activity?.AddEvent(new ActivityEvent("backup.failed", tags: new ActivityTagsCollection
        {
            { "duration_ms", stopwatch.ElapsedMilliseconds },
            { "error.type", ex.GetType().Name }
        }));

        LogBackupFailed(ex, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Translates Docker service names to localhost for local development.
    /// When the client runs on the host machine but connects to services in Docker,
    /// URLs containing Docker service hostnames (e.g., 'azurite') need to be translated to 'localhost'.
    /// </summary>
    private static Uri TranslateStorageUrlForLocalDevelopment(Uri originalUrl)
    {
        var urlString = originalUrl.ToString();
        
        // Replace common Docker service names with localhost
        // This allows the client running on the host to access services exposed from containers
        urlString = urlString.Replace("http://azurite:", "http://127.0.0.1:", StringComparison.OrdinalIgnoreCase);
        urlString = urlString.Replace("https://azurite:", "https://127.0.0.1:", StringComparison.OrdinalIgnoreCase);
        
        return new Uri(urlString);
    }

    /// <summary>
    /// Holds statistics for the backup operation.
    /// </summary>
    /// <summary>
    /// Running tallies for the backup operation. Deliberately counters only: the run's file set lives
    /// in the on-disk journal so memory use stays proportional to the current batch, not to the run.
    /// </summary>
    private class BackupStats
    {
        public long TotalBytes { get; set; }
        public int TotalScanned { get; set; }
        public int NewFilesCount { get; set; }
        public int ModifiedFilesCount { get; set; }
        public int UnchangedCount { get; set; }
        public int SkippedCount { get; set; }
        public int SkippedChangedCount { get; set; }
        public int SkippedSasExpiredCount { get; set; }
        public int TotalUploadAttempts { get; set; }
        public int TotalUploadFailures { get; set; }
    }

    private sealed record FileProcessingResult(
        BackupStats Stats,
        bool HasStartedBackupRun,
        Guid? RunId,
        BlobContainerClient? ManifestContainerClient,
        string? ManifestBasePath,
        bool ManifestIsPathEmbedded,
        SasUrlInfo? UploadSasUrlInfo,
        SasUrlInfo? ManifestSasUrlInfo,
        DateTimeOffset StartedAt);

    private sealed record BatchProcessingResult(
        bool HasStartedBackupRun,
        Guid? RunId,
        BlobContainerClient? ContainerClient,
        BlobContainerClient? ManifestContainerClient,
        string? ManifestBasePath,
        bool ManifestIsPathEmbedded,
        DateTimeOffset StartedAt,
        SasUrlInfo? UploadSasUrlInfo,
        SasUrlInfo? ManifestSasUrlInfo);

    private sealed record RunStartResult(
        Guid RunId,
        BlobContainerClient ContainerClient,
        BlobContainerClient ManifestContainerClient,
        string? ManifestBasePath,
        bool ManifestIsPathEmbedded,
        DateTimeOffset StartedAt,
        SasUrlInfo UploadSasUrlInfo,
        SasUrlInfo ManifestSasUrlInfo);

    private sealed record SasContext(
        BlobContainerClient ContainerClient,
        BlobContainerClient ManifestContainerClient,
        SasUrlInfo UploadSasUrlInfo,
        SasUrlInfo ManifestSasUrlInfo,
        string? ManifestBasePath,
        bool ManifestIsPathEmbedded);
}
