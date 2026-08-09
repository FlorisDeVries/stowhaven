using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Logging methods for BackupService.
/// </summary>
public partial class BackupService
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Backup operation started for device {DeviceId}, targets: {TargetDirectory}")]
    partial void LogBackupStarted(Guid deviceId, string targetDirectory);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Using ignore file: {IgnoreFilePath}")]
    partial void LogUsingIgnoreFile(string ignoreFilePath);

    [LoggerMessage(
        EventId = 25,
        Level = LogLevel.Information,
        Message = "Scanning {TargetCount} backup targets")]
    partial void LogScanningDirectories(int targetCount);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Scanned {FileCount} files")]
    partial void LogScannedFiles(int fileCount);

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
        EventId = 13,
        Level = LogLevel.Information,
        Message = "Backup run {RunId} committed")]
    partial void LogBackupRunCommitted(Guid runId);


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
        Message = "Smart hashing: {NewCount} new, {ModifiedCount} modified, {UnchangedCount} unchanged (skipped hashing), {SkippedCount} skipped (inaccessible)")]
    partial void LogSmartHashingStats(int newCount, int modifiedCount, int unchangedCount, int skippedCount);

    [LoggerMessage(
        EventId = 19,
        Level = LogLevel.Warning,
        Message = "Batch upload partial failure: {FailedCount}/{TotalCount} files failed to upload")]
    partial void LogBatchPartialFailure(int failedCount, int totalCount);

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

    [LoggerMessage(
        EventId = 29,
        Level = LogLevel.Warning,
        Message = "Backup failure rate approaching threshold: {FailedCount}/{TotalAttempts} files failed ({FailurePercentage:F1}%), threshold is {MaxPercentage}%")]
    partial void LogBackupFailureWarning(int failedCount, int totalAttempts, double failurePercentage, int maxPercentage);


    [LoggerMessage(
        EventId = 31,
        Level = LogLevel.Information,
        Message = "Scan progress: {Scanned:N0} files processed in {ElapsedMinutes:F1} min ({FilesPerSecond:N1} files/s), current target '{TargetName}' ({NeedsBackup:N0} to upload, {Unchanged:N0} unchanged, {Skipped:N0} skipped)")]
    partial void LogScanProgress(
        int scanned,
        int needsBackup,
        int unchanged,
        int skipped,
        string targetName,
        double elapsedMinutes,
        double filesPerSecond);

    [LoggerMessage(
        EventId = 32,
        Level = LogLevel.Information,
        Message = "Resuming pending backup run {RunId}; {UploadedCount} files already uploaded")]
    partial void LogPendingBackupRunResumed(Guid runId, int uploadedCount);

    [LoggerMessage(
        EventId = 33,
        Level = LogLevel.Warning,
        Message = "Pending backup run {RunId} expired at {ExpiresAt}; starting a new run")]
    partial void LogPendingBackupRunExpired(Guid runId, DateTimeOffset expiresAt);

    [LoggerMessage(
        EventId = 34,
        Level = LogLevel.Information,
        Message = "Finalized pending backup run {RunId} with commit {CommitId}")]
    partial void LogPendingBackupRunFinalized(Guid runId, Guid commitId);

    [LoggerMessage(
        EventId = 35,
        Level = LogLevel.Warning,
        Message = "Backup completed with {SkippedCount} skipped files under locked-file policy {LockedFilePolicy}. Treat this backup as degraded and review skipped-file warnings.")]
    partial void LogBackupCompletedWithSkippedFiles(int skippedCount, string lockedFilePolicy);

    [LoggerMessage(
        EventId = 36,
        Level = LogLevel.Warning,
        Message = "File changed during backup and was skipped this run: {FilePath} (scanned {ScannedSize} bytes, now {CurrentSize} bytes). It will be re-detected and backed up on the next run.")]
    partial void LogFileChangedDuringBackup(string filePath, long scannedSize, long currentSize);

    [LoggerMessage(
        EventId = 37,
        Level = LogLevel.Warning,
        Message = "Backup completed with {ChangedCount} file(s) skipped because they changed during the run; they will be backed up on the next run.")]
    partial void LogBackupCompletedWithChangedFiles(int changedCount);

    [LoggerMessage(
        EventId = 38,
        Level = LogLevel.Warning,
        Message = "Server committed the backup with errors: {FilesFailed} file(s) were skipped server-side (staged content did not match). {Detail}")]
    partial void LogCommitCompletedWithErrors(int filesFailed, string detail);

    [LoggerMessage(
        EventId = 39,
        Level = LogLevel.Information,
        Message = "Commit {CommitId} is still processing server-side after {WaitSeconds:F0}s (status {Status}). The run is durable and will be finalized automatically on the next backup run; no re-upload is needed.")]
    partial void LogCommitStillProcessing(Guid commitId, string status, double waitSeconds);

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Information,
        Message = "Upload SAS for run {RunId} is near expiry ({ExpiresAt}); requesting a fresh token before uploading the next batch.")]
    partial void LogRefreshingUploadSas(Guid runId, DateTimeOffset expiresAt);

    [LoggerMessage(
        EventId = 41,
        Level = LogLevel.Information,
        Message = "Refreshed upload SAS for run {RunId}; new expiry {ExpiresAt}.")]
    partial void LogRefreshedUploadSas(Guid runId, DateTimeOffset expiresAt);

    [LoggerMessage(
        EventId = 42,
        Level = LogLevel.Warning,
        Message = "SAS token expired mid-batch for run {RunId}; {Count} file(s) will be retried with a refreshed token.")]
    partial void LogSasExpiredMidBatch(int count, Guid runId);

    [LoggerMessage(
        EventId = 43,
        Level = LogLevel.Warning,
        Message = "{Count} file(s) for run {RunId} could not be uploaded even after refreshing the SAS token; they will be backed up on the next run.")]
    partial void LogSasExpiredUnrecovered(int count, Guid runId);

    [LoggerMessage(
        EventId = 44,
        Level = LogLevel.Warning,
        Message = "Backup completed with {Count} file(s) deferred because the upload SAS could not be kept valid; they will be backed up on the next run.")]
    partial void LogBackupCompletedWithSasExpiredFiles(int count);
}
