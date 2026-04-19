using System.Diagnostics;
using Dapr.Client;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Models.Events;
using FlorisDeV.BackupApi.Models.State;
using FlorisDeV.BackupApi.Telemetry;

namespace FlorisDeV.BackupApi.Services;

/// <summary>
/// Service responsible for publishing backup-related events to the message queue.
/// </summary>
public interface IBackupEventPublisher
{
    Task PublishBackupRunCommittedAsync(CommitJob commitJob, string manifestPath, CancellationToken cancellationToken = default);
}

public partial class BackupEventPublisher(
    DaprClient daprClient,
    ILogger<BackupEventPublisher> logger,
    TelemetryProvider telemetry
) : IBackupEventPublisher
{
    public async Task PublishBackupRunCommittedAsync(CommitJob commitJob, string manifestPath, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("PublishBackupRunCommittedEvent");
        activity?.SetTag(ActivityAttributes.OperationName, "PublishBackupRunCommittedEvent");
        activity?.SetTag(ActivityAttributes.DeviceId, commitJob.DeviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, commitJob.RunId.ToString());
        activity?.SetTag("commit_id", commitJob.CommitId.ToString());

        var stopwatch = Stopwatch.StartNew();
        var metricTags = new TagList { { "operation", "publish_event" }, { "event_type", "backup_run_committed" } };

        try
        {
            var backupEvent = new BackupRunCommittedEvent
            {
                CommitId = commitJob.CommitId,
                DeviceId = commitJob.DeviceId,
                RunId = commitJob.RunId,
                StartedAt = commitJob.CreatedAt, // Use commit creation time as reference
                CommittedAt = commitJob.CreatedAt,
                StagingPath = $"staging/{commitJob.DeviceId:N}/{commitJob.RunId:N}/",
                ManifestPath = manifestPath
            };

            LogPublishingEvent(logger, commitJob.DeviceId, commitJob.RunId, backupEvent.StagingPath, manifestPath);

            await daprClient.PublishEventAsync(
                DaprComponents.BackupEventsPubSub,
                "backup-events",
                backupEvent,
                cancellationToken);

            stopwatch.Stop();
            telemetry.BackupEventsPublished.Add(1, metricTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.AddEvent(new ActivityEvent("BackupRunCommittedEventPublished"));

            LogEventPublishedSuccess(logger, commitJob.DeviceId, commitJob.RunId);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var errorTags = new TagList
            {
                { "operation", "publish_event" },
                { "event_type", "backup_run_committed" },
                { "error.type", ex.GetType().Name }
            };
            telemetry.BackupEventsFailed.Add(1, errorTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, errorTags);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(ActivityAttributes.OperationStatus, "error");
            activity?.SetTag(ActivityAttributes.ErrorType, ex.GetType().Name);
            activity?.SetTag(ActivityAttributes.ErrorMessage, ex.Message);
            activity?.AddException(ex);

            LogEventPublishingFailed(logger, commitJob.DeviceId, commitJob.RunId, ex);

            throw;
        }
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, 
        "Publishing BackupRunCommitted event for device {deviceId}, run {runId}, staging path: {stagingPath}, manifest: {manifestPath}")]
    static partial void LogPublishingEvent(ILogger logger, Guid deviceId, Guid runId, string stagingPath, string manifestPath);

    [LoggerMessage(LogLevel.Information, 
        "Successfully published BackupRunCommitted event for device {deviceId}, run {runId}")]
    static partial void LogEventPublishedSuccess(ILogger logger, Guid deviceId, Guid runId);

    [LoggerMessage(LogLevel.Error, 
        "Failed to publish BackupRunCommitted event for device {deviceId}, run {runId}")]
    static partial void LogEventPublishingFailed(ILogger logger, Guid deviceId, Guid runId, Exception ex);

    #endregion
}
