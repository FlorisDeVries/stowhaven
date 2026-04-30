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
        EventId = 30,
        Level = LogLevel.Warning,
        Message = "High memory usage detected: {FileCount} files queued for backup ({TotalBytes} bytes). Consider adding exclusion patterns if backup is too large.")]
    partial void LogBackpressureWarning(int fileCount, long totalBytes);

    [LoggerMessage(
        EventId = 31,
        Level = LogLevel.Debug,
        Message = "Scan progress: {Scanned} files processed ({NeedsBackup} to upload, {Unchanged} unchanged, {Skipped} skipped)")]
    partial void LogScanProgress(int scanned, int needsBackup, int unchanged, int skipped);

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
}
