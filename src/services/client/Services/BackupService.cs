using System.Diagnostics;
using FlorisDeV.BackupClient.Telemetry;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupClient.Services;

public interface IBackupService
{
    Task<bool> Backup(CancellationToken cancellationToken);
}

public partial class BackupService(ILogger<BackupService> logger, TelemetryProvider telemetry) : IBackupService
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Backup operation started")]
    partial void LogBackupStarted();

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Backup operation completed successfully. Files: {FileCount}, Size: {SizeBytes} bytes, Duration: {DurationMs}ms")]
    partial void LogBackupCompleted(int fileCount, long sizeBytes, long durationMs);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "Backup operation failed after {DurationMs}ms")]
    partial void LogBackupFailed(Exception ex, long durationMs);

    public async Task<bool> Backup(CancellationToken cancellationToken)
    {
        using var activity = telemetry.ActivitySource.StartActivity();

        var backupType = "full";
        activity?.SetTag(ActivityAttributes.OperationName, "Backup");
        activity?.SetTag(ActivityAttributes.BackupType, backupType);

        LogBackupStarted();

        var stopwatch = Stopwatch.StartNew();
        var fileCount = 1; // Simulated file count
        var backupSizeBytes = 1024 * 1024 * 50L; // Simulated 50 MB

        var metricTags = new TagList
        {
            { "backup.type", backupType },
            { "operation.name", "Backup" }
        };

        try
        {
            // Simulate backup work
            await Task.Delay(1000, cancellationToken);
            await Task.Delay(100, cancellationToken);

            stopwatch.Stop();

            telemetry.CountFiles.Add(fileCount, metricTags);
            telemetry.BackupDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);
            telemetry.BackupSize.Record(backupSizeBytes, metricTags);

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag(ActivityAttributes.BackupSuccess, true);

            LogBackupCompleted(fileCount, backupSizeBytes, stopwatch.ElapsedMilliseconds);

            return true;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var failureTags = new TagList
            {
                { "backup.type", backupType },
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
}