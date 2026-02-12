using System.Diagnostics;
using FlorisDeV.BackupApi.Models.Application;
using FlorisDeV.BackupApi.Models.State;
using FlorisDeV.BackupApi.Telemetry;

namespace FlorisDeV.BackupApi.Services;

public interface IBackupRunService
{
    Task<BackupRunStartResult> StartBackupRunAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);
}

public class BackupRunService(
    IManifestManager manifestManager,
    ISasUrlService sasUrlService,
    TelemetryProvider telemetry
) : IBackupRunService
{
    public async Task<BackupRunStartResult> StartBackupRunAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("StartBackupRun");
        activity?.SetTag(ActivityAttributes.OperationName, "StartBackupRun");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());

        var stopwatch = Stopwatch.StartNew();
        var metricTags = new TagList { { "operation", "start_run" } };

        try
        {
            // Start Run
            var runId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow;
            activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

            var run = await manifestManager.CreateBackupRunAsync(deviceId, runId, startedAt, cancellationToken);

            // Create SaS URLs for upload
            var devicePath = $"staging/{deviceId:N}/{runId:N}/";
            var uploadSas = await sasUrlService.GenerateUploadSasUrlAsync(devicePath, ttlMinutes: 60, cancellationToken);

            var runStartDto = new BackupRunStartResult
            {
                Run = run,
                SasUrl = uploadSas
            };

            stopwatch.Stop();
            telemetry.BackupRunsStarted.Add(1, metricTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag(ActivityAttributes.BackupRunStatus, run.Status.ToString());

            // Return Run info + SaS URLs
            return runStartDto;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var errorTags = new TagList
            {
                { "operation", "start_run" },
                { "error.type", ex.GetType().Name }
            };
            telemetry.BackupRunsFailed.Add(1, errorTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, errorTags);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(ActivityAttributes.OperationStatus, "error");
            activity?.SetTag(ActivityAttributes.ErrorType, ex.GetType().Name);
            activity?.SetTag(ActivityAttributes.ErrorMessage, ex.Message);
            activity?.AddException(ex);

            throw;
        }
    }

    public async Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("CommitBackupRun");
        activity?.SetTag(ActivityAttributes.OperationName, "CommitBackupRun");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var stopwatch = Stopwatch.StartNew();
        var metricTags = new TagList { { "operation", "commit_run" } };

        try
        {
            // Commit Run in Manifest
            var run = await manifestManager.CommitBackupRunAsync(deviceId, runId, cancellationToken);

            stopwatch.Stop();
            telemetry.BackupRunsCommitted.Add(1, metricTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag(ActivityAttributes.BackupRunStatus, run.Status.ToString());

            // Queue worker for async post-processing (not implemented yet)

            return run;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var errorTags = new TagList
            {
                { "operation", "commit_run" },
                { "error.type", ex.GetType().Name }
            };
            telemetry.BackupRunsFailed.Add(1, errorTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, errorTags);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(ActivityAttributes.OperationStatus, "error");
            activity?.SetTag(ActivityAttributes.ErrorType, ex.GetType().Name);
            activity?.SetTag(ActivityAttributes.ErrorMessage, ex.Message);
            activity?.AddException(ex);

            throw;
        }
    }
}