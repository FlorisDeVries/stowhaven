using System.Diagnostics;
using Azure.Storage.Blobs;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Clients.BackupApi.DTOs;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Services;

public interface IBackupService
{
    Task<bool> Backup(CancellationToken cancellationToken);
}

public partial class BackupService(
    ILogger<BackupService> logger,
    TelemetryProvider telemetry,
    IBackupApiClient backupApiClient,
    IFileSystemService fileSystemService,
    IBackupStateService backupStateService,
    ResiliencePipelineProvider resiliencePipelines,
    IOptions<BackupClientOptions> backupOptions) : IBackupService
{
    private readonly BackupClientOptions _options = backupOptions.Value;

    /// <summary>
    /// Associates a file with its backup target for multi-directory support.
    /// </summary>
    internal record TaggedFile(string TargetName, string TargetDirectory, FileMetadata Metadata)
    {
        /// <summary>
        /// Gets the storage path: "{targetName}/{relativePath}"
        /// This ensures unique paths across multiple backup targets.
        /// </summary>
        public string GetStoragePath()
        {
            var relativePath = Path.GetRelativePath(TargetDirectory, Metadata.FilePath);
            // Normalize to forward slashes and prepend target name
            var normalizedRelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            return $"{TargetName}/{normalizedRelativePath}";
        }
    }

    public async Task<bool> Backup(CancellationToken cancellationToken)
    {
        var targets = _options.GetEffectiveTargets();
        
        // Step 0: Validate all backup targets before starting
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
            else if (validationResult.Severity == ValidationSeverity.Warning)
            {
                LogBackupValidationWarning($"Target '{targetName}': {validationResult.Message}");
            }
        }

        using var activity = telemetry.ActivitySource.StartActivity();

        // Get or create device state (generates persistent device ID on first run)
        var deviceState = await backupStateService.GetOrCreateDeviceStateAsync(cancellationToken);
        var deviceId = deviceState.DeviceId;

        activity?.SetTag(ActivityAttributes.OperationName, "Backup");
        activity?.SetTag("device.id", deviceId);
        activity?.SetTag("backup.targets", string.Join(", ", targets.Keys));

        LogBackupStarted(deviceId, string.Join(", ", targets.Select(t => $"{t.Key}={t.Value}")));

        var stopwatch = Stopwatch.StartNew();

        var metricTags = new TagList
        {
            { "operation.name", "Backup" }
        };

        try
        {
            // Step 1: Resolve exclusion patterns from .backupignore file and config
            var ignoreFilePath = _options.IgnoreFilePath;
            var excludePatterns = BackupIgnoreParser.GetCombinedPatterns(ignoreFilePath, _options.ExcludePatterns);

            if (!string.IsNullOrWhiteSpace(ignoreFilePath))
            {
                LogUsingIgnoreFile(ignoreFilePath);
            }

            // Step 2: Stream files from all targets and process with smart hashing + batching
            LogScanningDirectories(targets.Count);

            const int batchSize = 100; // Process 100 files at a time
            const long batchSizeBytes = 100 * 1024 * 1024; // Or 100 MB, whichever comes first

            var currentBatch = new List<TaggedFile>();
            var allChangedFiles = new List<TaggedFile>();
            var deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long currentBatchBytes = 0;
            long totalBytes = 0;
            var totalScanned = 0;
            var newFilesCount = 0;
            var modifiedFilesCount = 0;
            var unchangedCount = 0;
            var totalUploadAttempts = 0;
            var totalUploadFailures = 0;

            // Track if this is the first backup
            var backupType = deviceState.LastSuccessfulBackup == null ? "full" : "incremental";
            var hasStartedBackupRun = false;
            Guid? runId = null;
            BlobContainerClient? containerClient = null;

            // Scan all targets
            await foreach (var taggedFile in ScanAllTargetsAsync(targets, excludePatterns, cancellationToken))
            {
                totalScanned++;
                
                // Get storage path (includes target name prefix)
                var storagePath = taggedFile.GetStoragePath();
                scannedPaths.Add(storagePath);

                // Step 3: Smart hashing - only hash if needed
                var previousState = await backupStateService.GetFileStateAsync(storagePath, cancellationToken);

                TaggedFile fileToBackup;
                var needsBackup = false;

                if (previousState == null)
                {
                    // New file - needs hash and backup
                    var hash = await fileSystemService.ComputeFileHashAsync(taggedFile.Metadata.FilePath, cancellationToken);
                    fileToBackup = taggedFile with { Metadata = taggedFile.Metadata with { Hash = hash } };
                    needsBackup = true;
                    newFilesCount++;
                }
                else if (previousState.SizeBytes != taggedFile.Metadata.SizeBytes ||
                         previousState.LastModifiedUtc != taggedFile.Metadata.LastModified)
                {
                    // Modified file - needs hash and backup
                    var hash = await fileSystemService.ComputeFileHashAsync(taggedFile.Metadata.FilePath, cancellationToken);
                    fileToBackup = taggedFile with { Metadata = taggedFile.Metadata with { Hash = hash } };

                    // Only consider it modified if hash actually changed
                    if (hash != previousState.Sha256Hash)
                    {
                        needsBackup = true;
                        modifiedFilesCount++;
                    }
                    else
                    {
                        // Size/timestamp changed but content didn't (rare edge case)
                        fileToBackup = taggedFile with { Metadata = taggedFile.Metadata with { Hash = hash } };
                        unchangedCount++;
                    }
                }
                else
                {
                    // Unchanged - reuse existing hash (no I/O!)
                    fileToBackup = taggedFile with { Metadata = taggedFile.Metadata with { Hash = previousState.Sha256Hash } };
                    unchangedCount++;
                }

                if (needsBackup)
                {
                    currentBatch.Add(fileToBackup);
                    allChangedFiles.Add(fileToBackup);
                    currentBatchBytes += fileToBackup.Metadata.SizeBytes;
                    totalBytes += fileToBackup.Metadata.SizeBytes;
                }

                // Process batch when size threshold reached
                if (currentBatch.Count >= batchSize || currentBatchBytes >= batchSizeBytes)
                {
                    if (!hasStartedBackupRun)
                    {
                        // Start backup run on first batch with changes
                        var startRequest = new StartBackupRunRequest { DeviceId = deviceId };
                        var startResponse = await backupApiClient.StartBackupRun(startRequest, cancellationToken);
                        runId = startResponse.RunId;
                        // Create container client from SAS URL (URL not logged for security)
                        containerClient = new BlobContainerClient(startResponse.SasUrlInfo.Url);
                        hasStartedBackupRun = true;

                        activity?.SetTag(ActivityAttributes.BackupType, backupType);
                        metricTags.Add("backup.type", backupType);

                        LogBackupRunStarted(runId.Value, currentBatch.Count, currentBatchBytes);
                    }

                    // Upload batch - returns only successfully uploaded files for atomicity
                    totalUploadAttempts += currentBatch.Count;
                    var uploadedFiles = await UploadTaggedFilesToStagingAsync(containerClient!, currentBatch, cancellationToken);
                    var failedCount = currentBatch.Count - uploadedFiles.Count;
                    totalUploadFailures += failedCount;
                    
                    // Only save state for files that were actually uploaded successfully
                    if (uploadedFiles.Count > 0)
                    {
                        await backupStateService.UpsertTaggedFileStateBatchAsync(uploadedFiles, runId!.Value, cancellationToken);
                        LogBatchProcessed(uploadedFiles.Count, currentBatchBytes, totalScanned);
                    }
                    
                    // If some files failed, log warning
                    if (failedCount > 0)
                    {
                        LogBatchPartialFailure(failedCount, currentBatch.Count);
                    }

                    currentBatch.Clear();
                    currentBatchBytes = 0;
                }
            }

            LogScannedFiles(totalScanned);

            // Process final batch if any
            if (currentBatch.Count > 0)
            {
                if (!hasStartedBackupRun)
                {
                    // Start backup run
                    var startRequest = new StartBackupRunRequest { DeviceId = deviceId };
                    var startResponse = await backupApiClient.StartBackupRun(startRequest, cancellationToken);
                    runId = startResponse.RunId;
                    // Create container client from SAS URL (URL not logged for security)
                    containerClient = new BlobContainerClient(startResponse.SasUrlInfo.Url);
                    hasStartedBackupRun = true;

                    activity?.SetTag(ActivityAttributes.BackupType, backupType);
                    metricTags.Add("backup.type", backupType);

                    LogBackupRunStarted(runId.Value, currentBatch.Count, currentBatchBytes);
                }

                // Upload batch - returns only successfully uploaded files for atomicity
                totalUploadAttempts += currentBatch.Count;
                var uploadedFiles = await UploadTaggedFilesToStagingAsync(containerClient!, currentBatch, cancellationToken);
                var failedCount = currentBatch.Count - uploadedFiles.Count;
                totalUploadFailures += failedCount;
                
                // Only save state for files that were actually uploaded successfully
                if (uploadedFiles.Count > 0)
                {
                    await backupStateService.UpsertTaggedFileStateBatchAsync(uploadedFiles, runId!.Value, cancellationToken);
                    LogBatchProcessed(uploadedFiles.Count, currentBatchBytes, totalScanned);
                }
                
                // If some files failed, log warning
                if (failedCount > 0)
                {
                    LogBatchPartialFailure(failedCount, currentBatch.Count);
                }
            }

            // Step 4: Detect deleted files (in previous backup but not scanned)
            if (deviceState.LastSuccessfulBackup != null)
            {
                var previousFiles = await backupStateService.GetAllFileStatesAsync(cancellationToken);
                foreach (var previousFile in previousFiles)
                {
                    if (!scannedPaths.Contains(previousFile.RelativePath))
                    {
                        deletedFiles.Add(previousFile.RelativePath);
                    }
                }
            }

            LogDeltaComputed(newFilesCount, modifiedFilesCount, deletedFiles.Count);
            LogSmartHashingStats(newFilesCount, modifiedFilesCount, unchangedCount);

            // If no changes, skip backup
            if (!hasStartedBackupRun && deletedFiles.Count == 0)
            {
                LogNoChangesDetected();
                stopwatch.Stop();

                activity?.SetTag(ActivityAttributes.OperationStatus, "skipped");
                activity?.SetTag(ActivityAttributes.BackupSuccess, true);
                activity?.SetTag("backup.skipped", true);

                return true;
            }

            // Step 5: Commit the backup run if started
            if (hasStartedBackupRun)
            {
                var commitRequest = new CommitBackupRunRequest
                {
                    DeviceId = deviceId,
                    RunId = runId!.Value
                };
                await backupApiClient.CommitBackupRun(commitRequest, cancellationToken);

                LogBackupRunCommitted(runId.Value);

                // Update device state
                await backupStateService.SaveBackupSuccessAsync(
                    runId.Value,
                    $"backup-{runId.Value:N}",
                    allChangedFiles.Select(tf => tf.Metadata).ToList(),
                    cancellationToken);
            }

            // Step 6: Clean up deleted files from tracking
            if (deletedFiles.Count > 0)
            {
                await backupStateService.RemoveDeletedFilesAsync(deletedFiles.ToList(), cancellationToken);
                LogDeletedFilesTracked(deletedFiles.Count);
            }

            stopwatch.Stop();

            // Check failure rate and fail backup if it exceeds threshold
            if (totalUploadAttempts > 0)
            {
                var failurePercentage = (totalUploadFailures * 100.0) / totalUploadAttempts;
                
                if (failurePercentage > _options.MaxFailurePercentage)
                {
                    var errorMessage = $"Backup failed: {totalUploadFailures}/{totalUploadAttempts} files failed " +
                                     $"({failurePercentage:F1}%), exceeding {_options.MaxFailurePercentage}% threshold";
                    LogBackupFailureThresholdExceeded(totalUploadFailures, totalUploadAttempts, failurePercentage, _options.MaxFailurePercentage);
                    throw new InvalidOperationException(errorMessage);
                }
                
                if (totalUploadFailures > 0)
                {
                    LogBackupCompletedWithFailures(totalUploadAttempts - totalUploadFailures, totalUploadFailures, failurePercentage);
                }
            }

            var totalChangedFiles = newFilesCount + modifiedFilesCount;
            telemetry.CountFiles.Add(totalChangedFiles, metricTags);
            telemetry.BackupDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);
            telemetry.BackupSize.Record(totalBytes, metricTags);

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag(ActivityAttributes.BackupSuccess, true);
            if (runId.HasValue)
            {
                activity?.SetTag("backup.run_id", runId.Value);
            }
            activity?.SetTag("backup.files.new", newFilesCount);
            activity?.SetTag("backup.files.modified", modifiedFilesCount);
            activity?.SetTag("backup.files.deleted", deletedFiles.Count);
            activity?.SetTag("backup.files.unchanged", unchangedCount);

            LogBackupCompleted(totalChangedFiles, totalBytes, stopwatch.ElapsedMilliseconds);

            return true;
        }
        catch (Exception ex)
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
            throw;
        }
    }

    /// <summary>
    /// Scans all backup targets and yields files with their target metadata.
    /// </summary>
    private async IAsyncEnumerable<TaggedFile> ScanAllTargetsAsync(
        IReadOnlyDictionary<string, string> targets,
        string[]? excludePatterns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var (targetName, targetDirectory) in targets)
        {
            LogScanningTarget(targetName, targetDirectory);

            // Check for target-specific .backupignore
            var targetIgnorePath = Path.Combine(targetDirectory, ".backupignore");
            var targetExcludePatterns = File.Exists(targetIgnorePath)
                ? BackupIgnoreParser.GetCombinedPatterns(targetIgnorePath, excludePatterns)
                : excludePatterns;

            await foreach (var file in fileSystemService.ScanDirectoryStreamAsync(
                targetDirectory,
                targetExcludePatterns,
                cancellationToken))
            {
                yield return new TaggedFile(targetName, targetDirectory, file);
            }
        }
    }

    /// <summary>
    /// Uploads tagged files to the staging area in Azure Blob Storage using parallel uploads with retry logic.
    /// Files are uploaded with storage paths that include target prefixes.
    /// Returns only the files that were successfully uploaded for atomic state management.
    /// </summary>
    private async Task<IReadOnlyList<TaggedFile>> UploadTaggedFilesToStagingAsync(
        BlobContainerClient containerClient,
        IReadOnlyList<TaggedFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return Array.Empty<TaggedFile>();

        LogUploadingFiles(files.Count);

        var uploadedFiles = new List<TaggedFile>();
        var uploadedCount = 0;
        var throttler = new SemaphoreSlim(_options.MaxParallelUploads, _options.MaxParallelUploads);
        var progressLock = new object();

        var uploadTasks = files.Select(async taggedFile =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                // Get storage path (includes target name prefix)
                var storagePath = taggedFile.GetStoragePath();
                var blobClient = containerClient.GetBlobClient(storagePath);

                // Upload with Polly resilience pipeline for automatic retry with exponential backoff
                await resiliencePipelines.BlobUploadPipeline.ExecuteAsync(async ct =>
                {
                    // Create timeout for this upload attempt
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.BlobUploadTimeoutSeconds));

                    // Get file stream
                    await using var fileStream = await fileSystemService.GetFileStreamAsync(
                        taggedFile.Metadata.FilePath, timeoutCts.Token);
                    
                    if (taggedFile.Metadata.SizeBytes >= _options.LargeFileThresholdBytes)
                    {
                        // Track progress for large files
                        var progress = new Progress<long>(bytesTransferred =>
                        {
                            var percentage = taggedFile.Metadata.SizeBytes > 0 
                                ? (int)((bytesTransferred * 100) / taggedFile.Metadata.SizeBytes) 
                                : 0;
                            LogLargeFileProgress(taggedFile.Metadata.FilePath, bytesTransferred, 
                                taggedFile.Metadata.SizeBytes, percentage);
                        });

                        var uploadOptions = new Azure.Storage.Blobs.Models.BlobUploadOptions
                        {
                            ProgressHandler = progress
                        };

                        await blobClient.UploadAsync(fileStream, uploadOptions, timeoutCts.Token);
                    }
                    else
                    {
                        // Regular upload for smaller files
                        await blobClient.UploadAsync(fileStream, overwrite: true, timeoutCts.Token);
                    }
                }, cancellationToken);

                // Track successfully uploaded file
                lock (progressLock)
                {
                    uploadedFiles.Add(taggedFile);
                    uploadedCount++;

                    if (uploadedCount % 10 == 0 || uploadedCount == files.Count)
                    {
                        LogUploadProgress(uploadedCount, files.Count);
                    }
                }

                return (success: true, file: taggedFile, error: (Exception?)null);
            }
            catch (Exception ex)
            {
                // Log but don't throw - we want to continue uploading other files
                LogFileUploadFailed(taggedFile.Metadata.FilePath, ex);
                return (success: false, file: taggedFile, error: ex);
            }
            finally
            {
                throttler.Release();
            }
        }).ToList();

        // Wait for all uploads to complete
        var results = await Task.WhenAll(uploadTasks);

        // Check if any critical failures occurred
        var failures = results.Where(r => !r.success).ToList();
        if (failures.Count > 0)
        {
            LogUploadSummary(uploadedFiles.Count, failures.Count, files.Count);
        }
        else
        {
            LogUploadComplete(files.Count);
        }

        throttler.Dispose();

        return uploadedFiles;
    }

    /// <summary>
    /// Uploads files to the staging area in Azure Blob Storage using parallel uploads.
    /// Files are uploaded with their relative paths preserved.
    /// Returns only the files that were successfully uploaded for atomic state management.
    /// </summary>
    /// <returns>List of successfully uploaded files (for state tracking)</returns>
    private async Task<IReadOnlyList<FileMetadata>> UploadFilesToStagingAsync(
        BlobContainerClient containerClient,
        IReadOnlyList<FileMetadata> files,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return Array.Empty<FileMetadata>();

        LogUploadingFiles(files.Count);

        var uploadedFiles = new List<FileMetadata>();
        var uploadedCount = 0;
        var throttler = new SemaphoreSlim(_options.MaxParallelUploads, _options.MaxParallelUploads);
        var progressLock = new object();

        var uploadTasks = files.Select(async file =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                // Get relative path from base directory
                var relativePath = Path.GetRelativePath(baseDirectory, file.FilePath);

                // Normalize path separators for blob storage (always use forward slash)
                var blobPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');

                var blobClient = containerClient.GetBlobClient(blobPath);

                // Upload file with progress tracking for large files
                await using var fileStream = await fileSystemService.GetFileStreamAsync(file.FilePath, cancellationToken);
                
                if (file.SizeBytes >= _options.LargeFileThresholdBytes)
                {
                    // Track progress for large files
                    var progress = new Progress<long>(bytesTransferred =>
                    {
                        var percentage = file.SizeBytes > 0 ? (int)((bytesTransferred * 100) / file.SizeBytes) : 0;
                        LogLargeFileProgress(relativePath, bytesTransferred, file.SizeBytes, percentage);
                    });

                    var uploadOptions = new Azure.Storage.Blobs.Models.BlobUploadOptions
                    {
                        ProgressHandler = progress
                    };

                    await blobClient.UploadAsync(fileStream, uploadOptions, cancellationToken);
                }
                else
                {
                    // Regular upload for smaller files
                    await blobClient.UploadAsync(fileStream, overwrite: true, cancellationToken);
                }

                // Track successfully uploaded file
                lock (progressLock)
                {
                    uploadedFiles.Add(file);
                    uploadedCount++;

                    if (uploadedCount % 10 == 0 || uploadedCount == files.Count)
                    {
                        LogUploadProgress(uploadedCount, files.Count);
                    }
                }

                return (success: true, file, error: (Exception?)null);
            }
            catch (Exception ex)
            {
                // Log but don't throw - we want to continue uploading other files
                LogFileUploadFailed(file.FilePath, ex);
                return (success: false, file, error: ex);
            }
            finally
            {
                throttler.Release();
            }
        }).ToList();

        // Wait for all uploads to complete
        var results = await Task.WhenAll(uploadTasks);

        // Check if any critical failures occurred
        var failures = results.Where(r => !r.success).ToList();
        if (failures.Count > 0)
        {
            LogUploadSummary(uploadedFiles.Count, failures.Count, files.Count);
        }
        else
        {
            LogUploadComplete(files.Count);
        }

        throttler.Dispose();

        return uploadedFiles;
    }

    #region Logging

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Backup operation started for device {DeviceId}, target directory: {TargetDirectory}")]
    partial void LogBackupStarted(Guid deviceId, string targetDirectory);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Using ignore file: {IgnoreFilePath}")]
    partial void LogUsingIgnoreFile(string ignoreFilePath);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Scanning directory: {Directory}")]
    partial void LogScanningDirectory(string directory);

    [LoggerMessage(
        EventId = 25,
        Level = LogLevel.Information,
        Message = "Scanning {TargetCount} backup targets")]
    partial void LogScanningDirectories(int targetCount);

    [LoggerMessage(
        EventId = 26,
        Level = LogLevel.Information,
        Message = "Scanning target '{TargetName}': {Directory}")]
    partial void LogScanningTarget(string targetName, string directory);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Scanned {FileCount} files")]
    partial void LogScannedFiles(int fileCount);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Computing hashes for {FileCount} files")]
    partial void LogHashingFiles(int fileCount);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Hash computation complete")]
    partial void LogHashingComplete();

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Delta computed: {NewCount} new, {ModifiedCount} modified, {DeletedCount} deleted")]
    partial void LogDeltaComputed(int newCount, int modifiedCount, int deletedCount);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Information,
        Message = "No changes detected, skipping backup")]
    partial void LogNoChangesDetected();

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Information,
        Message = "Backup run {RunId} started: {FileCount} files, {TotalBytes} bytes")]
    partial void LogBackupRunStarted(Guid runId, int fileCount, long totalBytes);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "Uploading {FileCount} files to staging area")]
    partial void LogUploadingFiles(int fileCount);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Information,
        Message = "Upload progress: {UploadedCount}/{TotalCount} files")]
    partial void LogUploadProgress(int uploadedCount, int totalCount);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Information,
        Message = "Upload complete: {FileCount} files")]
    partial void LogUploadComplete(int fileCount);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Information,
        Message = "Backup run {RunId} committed")]
    partial void LogBackupRunCommitted(Guid runId);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Information,
        Message = "Tracked {Count} deleted files")]
    partial void LogDeletedFilesTracked(int count);

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Information,
        Message = "Backup completed successfully: {FileCount} files, {SizeBytes} bytes, {DurationMs}ms")]
    partial void LogBackupCompleted(int fileCount, long sizeBytes, long durationMs);

    [LoggerMessage(
        EventId = 16,
        Level = LogLevel.Error,
        Message = "Backup operation failed after {DurationMs}ms")]
    partial void LogBackupFailed(Exception ex, long durationMs);

    [LoggerMessage(
        EventId = 17,
        Level = LogLevel.Information,
        Message = "Processed batch: {FileCount} files, {SizeBytes} bytes ({TotalScanned} total scanned)")]
    partial void LogBatchProcessed(int fileCount, long sizeBytes, int totalScanned);

    [LoggerMessage(
        EventId = 18,
        Level = LogLevel.Information,
        Message = "Smart hashing: {NewCount} new, {ModifiedCount} modified, {UnchangedCount} unchanged (skipped hashing)")]
    partial void LogSmartHashingStats(int newCount, int modifiedCount, int unchangedCount);

    [LoggerMessage(
        EventId = 19,
        Level = LogLevel.Warning,
        Message = "Batch upload partial failure: {FailedCount}/{TotalCount} files failed to upload")]
    partial void LogBatchPartialFailure(int failedCount, int totalCount);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Error,
        Message = "Failed to upload file: {FilePath}")]
    partial void LogFileUploadFailed(string filePath, Exception ex);

    [LoggerMessage(
        EventId = 21,
        Level = LogLevel.Debug,
        Message = "Large file upload progress: {FileName} - {BytesTransferred}/{TotalBytes} bytes ({Percentage}%)")]
    partial void LogLargeFileProgress(string fileName, long bytesTransferred, long totalBytes, int percentage);

    [LoggerMessage(
        EventId = 22,
        Level = LogLevel.Warning,
        Message = "Upload summary: {SuccessCount} succeeded, {FailedCount} failed out of {TotalCount} total")]
    partial void LogUploadSummary(int successCount, int failedCount, int totalCount);

    [LoggerMessage(
        EventId = 23,
        Level = LogLevel.Error,
        Message = "Backup validation failed")]
    partial void LogBackupValidationFailed(Exception ex);

    [LoggerMessage(
        EventId = 24,
        Level = LogLevel.Warning,
        Message = "Backup validation warning: {Message}")]
    partial void LogBackupValidationWarning(string message);

    [LoggerMessage(
        EventId = 27,
        Level = LogLevel.Error,
        Message = "Backup failed: {FailedCount}/{TotalAttempts} files failed ({FailurePercentage:F1}%), exceeding {MaxPercentage}% threshold")]
    partial void LogBackupFailureThresholdExceeded(int failedCount, int totalAttempts, double failurePercentage, int maxPercentage);

    [LoggerMessage(
        EventId = 28,
        Level = LogLevel.Warning,
        Message = "Backup completed with partial failures: {SuccessCount} succeeded, {FailedCount} failed ({FailurePercentage:F1}%)")]
    partial void LogBackupCompletedWithFailures(int successCount, int failedCount, double failurePercentage);

    #endregion
}