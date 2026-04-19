using System.Diagnostics;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlorisDeV.BackupApi.Models.Events;
using FlorisDeV.BackupApi.Models.Manifest;
using FlorisDeV.BackupApi.Models.State;
using FlorisDeV.BackupApi.Telemetry;

namespace FlorisDeV.BackupApi.Services;

public interface IBackupProcessingService
{
    Task ProcessBackupRunAsync(BackupRunCommittedEvent backupEvent, CancellationToken cancellationToken = default);
}

public partial class BackupProcessingService(
    ILogger<BackupProcessingService> logger,
    IBlobStorageService blobStorageService,
    IManifestManager manifestManager,
    TelemetryProvider telemetry
) : IBackupProcessingService
{

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

            // Load the CommitJob and update status to Processing
            var commitJob = await manifestManager.GetCommitJobAsync(backupEvent.CommitId, cancellationToken);
            
            // IDEMPOTENCY CHECK: Verify the commit job hasn't already been processed
            if (commitJob.Status == CommitJobStatus.Succeeded)
            {
                LogAlreadyProcessed(logger, backupEvent.DeviceId, backupEvent.RunId, commitJob.Status);
                activity?.SetTag(ActivityAttributes.OperationStatus, "skipped");
                activity?.SetTag("skip_reason", "already_succeeded");
                return; // Already processed successfully, skip
            }

            if (commitJob.Status == CommitJobStatus.Processing)
            {
                LogConcurrentProcessing(logger, backupEvent.DeviceId, backupEvent.RunId);
                // Another instance is processing, let it complete
                activity?.SetTag(ActivityAttributes.OperationStatus, "skipped");
                activity?.SetTag("skip_reason", "concurrent_processing");
                return;
            }

            // Update CommitJob status to Processing
            commitJob.Status = CommitJobStatus.Processing;
            commitJob = await manifestManager.UpdateCommitJobAsync(commitJob, cancellationToken);

            // Update BackupRun status to Processing
            var run = await manifestManager.GetBackupRunAsync(backupEvent.DeviceId, backupEvent.RunId, cancellationToken);
            run.Status = BackupRunStatus.Processing;
            await manifestManager.UpdateBackupRunAsync(
                backupEvent.DeviceId, 
                backupEvent.RunId, 
                run, 
                cancellationToken);

            // Download and parse run-manifest.json
            var manifestPath = backupEvent.ManifestPath ?? $"runs/{backupEvent.DeviceId:N}/{backupEvent.RunId:N}/run-manifest.json";
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
        ManifestFileEntry fileEntry,
        BlobContainerClient containerClient,
        CancellationToken cancellationToken)
    {
        LogProcessingFileEntry(logger, fileEntry.RelativePath, fileEntry.UniqueFileId);

        // Check if file already exists
        var existingFile = await manifestManager.GetFileEntryAsync(deviceId, fileEntry.RelativePath, cancellationToken);

        // Move blob from staging to files/
        var sourceBlobName = $"staging/{deviceId:N}/{runId:N}/{fileEntry.UniqueFileId}";
        var destinationBlobName = $"devices/{deviceId:N}/files/{fileEntry.UniqueFileId}";

        await MoveBlobAsync(containerClient, sourceBlobName, destinationBlobName, cancellationToken);

        // Create new FileVersion (Active)
        var newVersion = new FileVersion
        {
            DeviceId = deviceId.ToString("N"),
            UniqueFileId = fileEntry.UniqueFileId,
            RelativePath = fileEntry.RelativePath,
            Sha256 = fileEntry.Sha256,
            Size = fileEntry.Size,
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
            DeviceId = deviceId.ToString("N"),
            RelativePath = fileEntry.RelativePath,
            CurrentVersionId = fileEntry.UniqueFileId,
            Size = fileEntry.Size,
            LastWriteUtc = fileEntry.Mtime,
            LastBackupRunId = runId.ToString("N"),
            IsDeleted = false,
            ETag = existingFile?.ETag // Preserve ETag if updating
        };
        await manifestManager.SaveFileEntryAsync(fileEntryRecord, cancellationToken);

        LogFileEntryProcessed(logger, fileEntry.RelativePath, fileEntry.UniqueFileId);
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

        await MoveBlobAsync(containerClient, sourceBlobName, destinationBlobName, cancellationToken);

        // Update FileVersion state to Retired
        var retiredVersion = fileVersion with 
        { 
            State = FileVersionState.Retired,
            RetiredAt = DateTimeOffset.UtcNow
        };
        await manifestManager.SaveFileVersionAsync(retiredVersion, cancellationToken);

        LogFileVersionRetired(logger, uniqueFileId);
    }

    private async Task MoveBlobAsync(
        BlobContainerClient containerClient,
        string sourceBlobName,
        string destinationBlobName,
        CancellationToken cancellationToken)
    {
        var sourceBlobClient = containerClient.GetBlobClient(sourceBlobName);
        var destinationBlobClient = containerClient.GetBlobClient(destinationBlobName);

        // Check if source exists
        if (!await sourceBlobClient.ExistsAsync(cancellationToken))
        {
            LogSourceBlobNotFound(logger, sourceBlobName);
            throw new InvalidOperationException($"Source blob not found: {sourceBlobName}");
        }

        // For ADLS Gen2 (HNS ON), we can use rename operation (no early deletion fees)
        // For standard storage, we use copy + delete
        var isAzurite = await blobStorageService.IsUsingAzuriteAsync(cancellationToken);
        
        if (!isAzurite)
        {
            // Try ADLS Gen2 rename API (DataLakeFileClient)
            try
            {
                var dataLakeServiceClient = await blobStorageService.GetDataLakeServiceClientAsync(cancellationToken);
                var fileSystemClient = dataLakeServiceClient.GetFileSystemClient(await blobStorageService.GetContainerNameAsync(cancellationToken));
                var sourceFileClient = fileSystemClient.GetFileClient(sourceBlobName);
                
                await sourceFileClient.RenameAsync(destinationBlobName, cancellationToken: cancellationToken);
                LogBlobMoved(logger, sourceBlobName, destinationBlobName);
                return;
            }
            catch (Exception ex)
            {
                // Fall back to copy + delete if rename fails
                LogFallingBackToCopyDelete(logger, sourceBlobName, ex);
            }
        }

        // Fallback: Copy + Delete
        var copyOperation = await destinationBlobClient.StartCopyFromUriAsync(
            sourceBlobClient.Uri,
            cancellationToken: cancellationToken);

        await copyOperation.WaitForCompletionAsync(cancellationToken);

        await sourceBlobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);

        LogBlobMoved(logger, sourceBlobName, destinationBlobName);
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

    [LoggerMessage(LogLevel.Warning, "Source blob not found: {sourceBlobName}")]
    static partial void LogSourceBlobNotFound(ILogger logger, string sourceBlobName);

    [LoggerMessage(LogLevel.Information, "Blob moved from {sourceBlobName} to {destinationBlobName}")]
    static partial void LogBlobMoved(ILogger logger, string sourceBlobName, string destinationBlobName);

    [LoggerMessage(LogLevel.Warning, "Falling back to copy+delete for blob {sourceBlobName}: {exception}")]
    static partial void LogFallingBackToCopyDelete(ILogger logger, string sourceBlobName, Exception exception);

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

    #endregion
}
