using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Logging methods for BackupStateService.
/// </summary>
public partial class BackupStateService
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Database initialized successfully")]
    private partial void LogDatabaseInitialized();

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Device state loaded: DeviceId={DeviceId}, TotalFiles={TotalFiles}")]
    private partial void LogDeviceStateLoaded(Guid deviceId, long totalFiles);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "New device state created: DeviceId={DeviceId}")]
    private partial void LogDeviceStateCreated(Guid deviceId);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Backup state saved: RunId={RunId}, Files={FileCount}, Bytes={TotalBytes}")]
    private partial void LogBackupStateSaved(Guid runId, int fileCount, long totalBytes);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Removed {Count} deleted file records from state")]
    private partial void LogDeletedFilesRemoved(int count);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Batch upserted {Count} file states for run {RunId}")]
    private partial void LogFileStatesBatchUpserted(int count, Guid runId);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Recorded {Count} deleted file(s) for run {RunId}")]
    private partial void LogDeletedFilesRecorded(int count, Guid runId);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "Local state schema upgraded from v{FromVersion} to v{ToVersion}; dropped {DroppedRuns} unresumable in-flight run journal(s).")]
    private partial void LogSchemaUpgraded(long fromVersion, long toVersion, int droppedRuns);
}
