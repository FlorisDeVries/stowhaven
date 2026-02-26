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
    IOptions<BackupClientOptions> backupOptions) : IBackupService
{
    private readonly BackupClientOptions _options = backupOptions.Value;

    public async Task<bool> Backup(CancellationToken cancellationToken)
    {
        using var activity = telemetry.ActivitySource.StartActivity();

        // Get or create device state (generates persistent device ID on first run)
        var deviceState = await backupStateService.GetOrCreateDeviceStateAsync(cancellationToken);
        var deviceId = deviceState.DeviceId;

        activity?.SetTag(ActivityAttributes.OperationName, "Backup");
        activity?.SetTag("device.id", deviceId);

        LogBackupStarted(deviceId, _options.BackupTargetDirectory);

        var stopwatch = Stopwatch.StartNew();

        var metricTags = new TagList
        {
            { "operation.name", "Backup" }
        };

        try
        {
            // Step 1: Resolve exclusion patterns from .backupignore file and config
            var ignoreFilePath = _options.IgnoreFilePath;
            if (string.IsNullOrWhiteSpace(ignoreFilePath))
            {
                // Default: look for .backupignore in the target directory
                var defaultIgnoreFile = Path.Combine(_options.BackupTargetDirectory, ".backupignore");
                ignoreFilePath = File.Exists(defaultIgnoreFile) ? defaultIgnoreFile : null;
            }

            var excludePatterns = BackupIgnoreParser.GetCombinedPatterns(ignoreFilePath, _options.ExcludePatterns);

            if (!string.IsNullOrWhiteSpace(ignoreFilePath))
            {
                LogUsingIgnoreFile(ignoreFilePath);
            }

            // Step 2: Scan files in target directory
            LogScanningDirectory(_options.BackupTargetDirectory);
            var allFiles = await fileSystemService.ScanDirectoryAsync(
                _options.BackupTargetDirectory,
                excludePatterns,
                cancellationToken);

            LogScannedFiles(allFiles.Count);

            // Step 3: Hash files to detect content changes
            LogHashingFiles(allFiles.Count);
            var hashedFiles = new List<FileMetadata>();
            foreach (var file in allFiles)
            {
                var hash = await fileSystemService.ComputeFileHashAsync(file.FilePath, cancellationToken);
                hashedFiles.Add(file with { Hash = hash });
            }
            LogHashingComplete();

            // Step 4: Compute delta (what changed since last backup)
            var delta = await backupStateService.ComputeDeltaAsync(hashedFiles, cancellationToken);
            var totalChangedFiles = delta.NewFiles.Count + delta.ModifiedFiles.Count;

            LogDeltaComputed(delta.NewFiles.Count, delta.ModifiedFiles.Count, delta.DeletedFiles.Count);

            // If no changes, skip backup
            if (totalChangedFiles == 0 && delta.DeletedFiles.Count == 0)
            {
                LogNoChangesDetected();
                stopwatch.Stop();

                activity?.SetTag(ActivityAttributes.OperationStatus, "skipped");
                activity?.SetTag(ActivityAttributes.BackupSuccess, true);
                activity?.SetTag("backup.skipped", true);

                return true;
            }

            var backupType = deviceState.LastSuccessfulBackup == null ? "full" : "incremental";
            activity?.SetTag(ActivityAttributes.BackupType, backupType);
            metricTags.Add("backup.type", backupType);

            // Step 5: Start backup run - get SAS URL for upload
            var startRequest = new StartBackupRunRequest { DeviceId = deviceId };
            var startResponse = await backupApiClient.StartBackupRun(startRequest, cancellationToken);

            LogBackupRunStarted(startResponse.RunId, totalChangedFiles, delta.TotalBytes);

            // Step 6: Upload changed files to staging area
            var filesToUpload = delta.NewFiles.Concat(delta.ModifiedFiles).ToList();
            await UploadFilesToStagingAsync(
                startResponse.SasUrlInfo.Url,
                filesToUpload,
                _options.BackupTargetDirectory,
                cancellationToken);

            // Step 7: Commit the backup run
            var commitRequest = new CommitBackupRunRequest
            {
                DeviceId = deviceId,
                RunId = startResponse.RunId
            };
            await backupApiClient.CommitBackupRun(commitRequest, cancellationToken);

            LogBackupRunCommitted(startResponse.RunId);

            // Step 8: Save backup success state
            await backupStateService.SaveBackupSuccessAsync(
                startResponse.RunId,
                $"backup-{startResponse.RunId:N}",
                filesToUpload,
                cancellationToken);

            // Step 9: Clean up deleted files from tracking
            if (delta.DeletedFiles.Count > 0)
            {
                await backupStateService.RemoveDeletedFilesAsync(delta.DeletedFiles, cancellationToken);
                LogDeletedFilesTracked(delta.DeletedFiles.Count);
            }

            stopwatch.Stop();

            telemetry.CountFiles.Add(totalChangedFiles, metricTags);
            telemetry.BackupDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);
            telemetry.BackupSize.Record(delta.TotalBytes, metricTags);

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag(ActivityAttributes.BackupSuccess, true);
            activity?.SetTag("backup.run_id", startResponse.RunId);
            activity?.SetTag("backup.files.new", delta.NewFiles.Count);
            activity?.SetTag("backup.files.modified", delta.ModifiedFiles.Count);
            activity?.SetTag("backup.files.deleted", delta.DeletedFiles.Count);

            LogBackupCompleted(totalChangedFiles, delta.TotalBytes, stopwatch.ElapsedMilliseconds);

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
    /// Uploads files to the staging area in Azure Blob Storage using the provided SAS URL.
    /// Files are uploaded with their relative paths preserved.
    /// </summary>
    private async Task UploadFilesToStagingAsync(
        Uri sasUrl,
        IReadOnlyList<FileMetadata> files,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return;

        var containerClient = new BlobContainerClient(sasUrl);

        LogUploadingFiles(files.Count);

        var uploadedCount = 0;
        foreach (var file in files)
        {
            // Get relative path from base directory
            var relativePath = Path.GetRelativePath(baseDirectory, file.FilePath);

            // Normalize path separators for blob storage (always use forward slash)
            var blobPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');

            var blobClient = containerClient.GetBlobClient(blobPath);

            // Upload file with progress tracking
            await using var fileStream = await fileSystemService.GetFileStreamAsync(file.FilePath, cancellationToken);
            await blobClient.UploadAsync(fileStream, overwrite: true, cancellationToken);

            uploadedCount++;

            if (uploadedCount % 10 == 0 || uploadedCount == files.Count)
            {
                LogUploadProgress(uploadedCount, files.Count);
            }
        }

        LogUploadComplete(files.Count);
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

    #endregion
}