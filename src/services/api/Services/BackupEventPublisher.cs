using System.Diagnostics;
using Dapr.Client;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Constants;
using FlorisDeV.BackupContracts.Events;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Logging.OpenTelemetry;

namespace FlorisDeV.BackupApi.Services;

/// <summary>
/// Service responsible for publishing backup-related events to the message queue.
/// </summary>
public interface IBackupEventPublisher
{
    Task PublishBackupRunCommittedAsync(CommitJob commitJob, CancellationToken cancellationToken = default);
}

public partial class BackupEventPublisher(
    DaprClient daprClient,
    ILogger<BackupEventPublisher> logger,
    TelemetryProvider telemetry
) : IBackupEventPublisher
{
    public async Task PublishBackupRunCommittedAsync(CommitJob commitJob, CancellationToken cancellationToken = default)
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
            var manifestPath = GetManifestPath(commitJob.DeviceId, commitJob.RunId);
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

            await daprClient.InvokeBindingAsync(
                DaprComponents.BackupEventsOutputBinding,
                "create",
                backupEvent,
                cancellationToken: cancellationToken);

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

    private static string GetManifestPath(Guid deviceId, Guid runId) => $"runs/{deviceId:N}/{runId:N}/run-manifest.json";

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
