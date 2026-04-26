using System.Diagnostics;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Telemetry;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly BackupClientOptions _options = backupOptions.Value;

    public async Task<bool> Backup(CancellationToken cancellationToken)
    {
        var targets = _options.GetEffectiveTargets();

        // Step 0: Validate all backup targets before starting
        ValidateTargets(targets);

        using var activity = telemetry.ActivitySource.StartActivity();

        // Get or create device state (generates persistent device ID on first run)
        var deviceState = await backupStateService.GetOrCreateDeviceStateAsync(cancellationToken);
        var deviceId = deviceState.DeviceId;

        await RegisterDeviceAsync(deviceId, cancellationToken);

        SetupTelemetryBackupStart(activity, deviceId, targets);

        var stopwatch = Stopwatch.StartNew();
        var metricTags = new TagList { { "operation.name", "Backup" } };

        try
        {
            // Step 1: Resolve exclusion patterns
            var excludePatterns = ResolveExclusionPatterns();

            // Step 2: Scan, hash, batch, and upload files
            var processingResult = await ProcessFilesAsync(
                deviceState, deviceId, targets, excludePatterns, activity, metricTags, cancellationToken);

            // Step 3: Detect deleted files
            var deletedFiles = await DetectDeletedFilesAsync(deviceState, processingResult.Stats.ScannedPaths, cancellationToken);

            LogDeltaComputed(processingResult.Stats.NewFilesCount, processingResult.Stats.ModifiedFilesCount, deletedFiles.Count);
            LogSmartHashingStats(processingResult.Stats.NewFilesCount, processingResult.Stats.ModifiedFilesCount, processingResult.Stats.UnchangedCount, processingResult.Stats.SkippedCount);

            // Step 4: Handle case of no changes
            if (!processingResult.HasStartedBackupRun && deletedFiles.Count == 0)
            {
                return HandleNoChanges(activity, stopwatch);
            }

            if (!processingResult.HasStartedBackupRun && deletedFiles.Count > 0)
            {
                processingResult = await StartDeletionOnlyRunAsync(
                    processingResult,
                    deviceId,
                    activity,
                    metricTags,
                    cancellationToken);
            }

            // Step 5: Commit backup run and update state
            await CommitBackupAsync(
                processingResult.HasStartedBackupRun,
                processingResult.RunId,
                deviceId,
                processingResult.Stats.UploadedChangedFiles,
                deletedFiles,
                processingResult.ManifestContainerClient,
                processingResult.ManifestBasePath,
                processingResult.ManifestIsPathEmbedded,
                cancellationToken);

            // Step 6: Validate failure threshold and record metrics
            ValidateFailureThreshold(processingResult.Stats.TotalUploadAttempts, processingResult.Stats.TotalUploadFailures);

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
            Activity? activity,
            TagList metricTags,
            CancellationToken cancellationToken)
    {
        LogScanningDirectories(targets.Count);

        const int batchSize = 100;
        const long batchSizeBytes = 100 * 1024 * 1024; // 100 MB

        var stats = new BackupStats();
        var currentBatch = new List<TaggedFile>();
        long currentBatchBytes = 0;

        var backupType = deviceState.LastSuccessfulBackup == null ? BackupType.Full : BackupType.Incremental;
        var hasStartedBackupRun = false;
        Guid? runId = null;
        BlobContainerClient? containerClient = null;
        BlobContainerClient? manifestContainerClient = null;
        string? manifestBasePath = null;
        bool manifestIsPathEmbedded = false;

        await foreach (var taggedFile in scanner.ScanAllTargetsAsync(targets, excludePatterns, cancellationToken))
        {
            stats.TotalScanned++;
            stats.ScannedPaths.Add(taggedFile.GetStoragePath());

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
                    continue; // Skip this file entirely
            }

            if (stats.TotalScanned % 1000 == 0)
            {
                LogScanProgress(stats.TotalScanned,
                    stats.NewFilesCount + stats.ModifiedFilesCount,
                    stats.UnchangedCount,
                    stats.SkippedCount);
            }

            if (needsBackup)
            {
                var stagedFile = fileWithHash with
                {
                    UniqueFileId = GenerateUniqueFileId(fileWithHash.Metadata.Hash)
                };

                currentBatch.Add(stagedFile);
                stats.AllChangedFiles.Add(stagedFile);
                currentBatchBytes += stagedFile.Metadata.SizeBytes;
                stats.TotalBytes += stagedFile.Metadata.SizeBytes;

                // Backpressure warning: detect unbounded memory growth
                // Warn every 10,000 files to indicate potential memory pressure
                if (stats.AllChangedFiles.Count % 10000 == 0 && stats.AllChangedFiles.Count > 0)
                {
                    LogBackpressureWarning(stats.AllChangedFiles.Count, stats.TotalBytes);
                }
            }

            if (currentBatch.Count >= batchSize || currentBatchBytes >= batchSizeBytes)
            {
                var batchResult = await ProcessBatchAsync(
                    hasStartedBackupRun, runId, containerClient, manifestContainerClient, manifestBasePath, manifestIsPathEmbedded, deviceId, backupType,
                    currentBatch, currentBatchBytes, stats, activity, metricTags, cancellationToken);

                hasStartedBackupRun = batchResult.HasStartedBackupRun;
                runId = batchResult.RunId;
                containerClient = batchResult.ContainerClient;
                manifestContainerClient = batchResult.ManifestContainerClient;
                manifestBasePath = batchResult.ManifestBasePath;
                manifestIsPathEmbedded = batchResult.ManifestIsPathEmbedded;

                currentBatch.Clear();
                currentBatchBytes = 0;
            }
        }

        LogScannedFiles(stats.TotalScanned);

        if (currentBatch.Count > 0)
        {
            var batchResult = await ProcessBatchAsync(
                hasStartedBackupRun, runId, containerClient, manifestContainerClient, manifestBasePath, manifestIsPathEmbedded, deviceId, backupType,
                currentBatch, currentBatchBytes, stats, activity, metricTags, cancellationToken);

            hasStartedBackupRun = batchResult.HasStartedBackupRun;
            runId = batchResult.RunId;
            containerClient = batchResult.ContainerClient;
            manifestContainerClient = batchResult.ManifestContainerClient;
            manifestBasePath = batchResult.ManifestBasePath;
            manifestIsPathEmbedded = batchResult.ManifestIsPathEmbedded;
        }

        return new FileProcessingResult(
            stats,
            hasStartedBackupRun,
            runId,
            manifestContainerClient,
            manifestBasePath,
            manifestIsPathEmbedded);
    }

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
            hasStartedBackupRun = true;
        }

        // Upload batch
        stats.TotalUploadAttempts += currentBatch.Count;
        var uploadedFiles = await uploader.UploadFilesAsync(containerClient!, currentBatch, cancellationToken);
        var failedCount = currentBatch.Count - uploadedFiles.Count;
        stats.TotalUploadFailures += failedCount;

        // Check failure threshold after each batch (early detection)
        CheckFailureThresholdProgress(stats.TotalUploadAttempts, stats.TotalUploadFailures);

        // Local file state is updated only after the server-side commit succeeds.
        stats.UploadedChangedFiles.AddRange(uploadedFiles);
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
            manifestIsPathEmbedded);
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
            ManifestIsPathEmbedded = runStart.ManifestIsPathEmbedded
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
            manifestSasInfo.IsPathEmbedded);
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

    private async Task<HashSet<string>> DetectDeletedFilesAsync(
        DeviceState deviceState,
        HashSet<string> scannedPaths,
        CancellationToken cancellationToken)
    {
        if (deviceState.LastSuccessfulBackup == null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var deletedFilesList = await scanner.DetectDeletedFilesAsync(scannedPaths, cancellationToken);
        return new HashSet<string>(deletedFilesList, StringComparer.OrdinalIgnoreCase);
    }

    private bool HandleNoChanges(Activity? activity, Stopwatch stopwatch)
    {
        LogNoChangesDetected();
        stopwatch.Stop();

        activity?.SetTag(ActivityAttributes.OperationStatus, "skipped");
        activity?.SetTag(ActivityAttributes.BackupSuccess, true);
        activity?.SetTag("backup.skipped", true);

        return true;
    }

    private async Task CommitBackupAsync(
        bool hasStartedBackupRun,
        Guid? runId,
        Guid deviceId,
        List<TaggedFile> allChangedFiles,
        HashSet<string> deletedFiles,
        BlobContainerClient? manifestContainerClient,
        string? manifestBasePath,
        bool manifestIsPathEmbedded,
        CancellationToken cancellationToken)
    {
        if (!hasStartedBackupRun)
            return;

        if (manifestContainerClient == null)
            throw new InvalidOperationException("Manifest upload client was not initialized for the backup run");

        var manifestPath = $"runs/{deviceId:N}/{runId!.Value:N}/run-manifest.json";
        var manifest = BuildRunManifest(deviceId, runId.Value, allChangedFiles, deletedFiles);

        await uploader.UploadRunManifestAsync(
            manifestContainerClient,
            manifest,
            manifestBasePath,
            manifestIsPathEmbedded,
            cancellationToken);

        var commitRequest = new CommitBackupRunRequest
        {
            RunId = runId.Value
        };
        var commitResponse = await backupApiClient.CommitBackupRun(deviceId, commitRequest, cancellationToken);

        LogBackupRunCommitted(runId.Value);

        await WaitForCommitSucceededAsync(deviceId, commitResponse.CommitId, cancellationToken);

        if (allChangedFiles.Count > 0)
        {
            await backupStateService.UpsertFileStateBatchAsync(allChangedFiles, runId.Value, cancellationToken);
        }

        await CleanupDeletedFilesAsync(deletedFiles, cancellationToken);

        await backupStateService.SaveBackupSuccessAsync(
            runId.Value,
            commitResponse.CommitId.ToString("N"),
            [],
            cancellationToken);
    }

    private async Task WaitForCommitSucceededAsync(Guid deviceId, Guid commitId, CancellationToken cancellationToken)
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
                    return;
                case CommitJobStatus.Failed:
                    throw new InvalidOperationException($"Backup commit {commitId} failed: {status.Error ?? "Unknown error"}");
            }

            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"Backup commit {commitId} did not complete within {timeout.TotalSeconds:N0} seconds");
            }

            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    private static RunManifest BuildRunManifest(
        Guid deviceId,
        Guid runId,
        IReadOnlyList<TaggedFile> changedFiles,
        IReadOnlySet<string> deletedFiles)
    {
        return new RunManifest
        {
            DeviceId = deviceId.ToString("N"),
            RunId = runId.ToString("N"),
            Files = changedFiles.Select(file => new ManifestFileEntry
            {
                TargetName = file.TargetName,
                RelativePath = file.GetRelativePath(),
                UniqueFileId = file.UniqueFileId
                    ?? throw new InvalidOperationException($"Missing unique file ID for {file.GetStoragePath()}"),
                Sha256 = file.GetUploadSha256(),
                Size = file.GetUploadSizeBytes(),
                Mtime = file.Metadata.LastModified,
                Encryption = file.Encryption
            }).ToList(),
            Deleted = deletedFiles.Order(StringComparer.OrdinalIgnoreCase).ToList()
        };
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

    private async Task CleanupDeletedFilesAsync(HashSet<string> deletedFiles, CancellationToken cancellationToken)
    {
        if (deletedFiles.Count > 0)
        {
            await backupStateService.RemoveDeletedFilesAsync(deletedFiles.ToList(), cancellationToken);
            LogDeletedFilesTracked(deletedFiles.Count);
        }
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
    }

    private void HandleBackupFailure(Exception ex, Activity? activity, Stopwatch stopwatch)
    {
        stopwatch.Stop();

        var failureTags = new TagList
        {
            { "operation.name", "Backup" },
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
    private class BackupStats
    {
        public List<TaggedFile> AllChangedFiles { get; } = new();
        public List<TaggedFile> UploadedChangedFiles { get; } = new();
        public HashSet<string> ScannedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long TotalBytes { get; set; }
        public int TotalScanned { get; set; }
        public int NewFilesCount { get; set; }
        public int ModifiedFilesCount { get; set; }
        public int UnchangedCount { get; set; }
        public int SkippedCount { get; set; }
        public int TotalUploadAttempts { get; set; }
        public int TotalUploadFailures { get; set; }
    }

    private sealed record FileProcessingResult(
        BackupStats Stats,
        bool HasStartedBackupRun,
        Guid? RunId,
        BlobContainerClient? ManifestContainerClient,
        string? ManifestBasePath,
        bool ManifestIsPathEmbedded);

    private sealed record BatchProcessingResult(
        bool HasStartedBackupRun,
        Guid? RunId,
        BlobContainerClient? ContainerClient,
        BlobContainerClient? ManifestContainerClient,
        string? ManifestBasePath,
        bool ManifestIsPathEmbedded);

    private sealed record RunStartResult(
        Guid RunId,
        BlobContainerClient ContainerClient,
        BlobContainerClient ManifestContainerClient,
        string? ManifestBasePath,
        bool ManifestIsPathEmbedded);
}