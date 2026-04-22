using System.Diagnostics;
using FlorisDeV.BackupApi.Models.Application;
using FlorisDeV.BackupApi.Models.State;
using FlorisDeV.BackupApi.Telemetry;

namespace FlorisDeV.BackupApi.Services;

public interface IBackupRunService
{
    Task<BackupRunStartResult> StartBackupRunAsync(Guid deviceId, string? clientIp = null, CancellationToken cancellationToken = default);
    Task<CommitJob> CommitBackupRunAsync(Guid deviceId, Guid runId, string? manifestPath = null, CancellationToken cancellationToken = default);
    Task<CommitJob> GetCommitStatusAsync(Guid commitId, CancellationToken cancellationToken = default);
}

public class BackupRunService(
    IManifestManager manifestManager,
    ISasUrlService sasUrlService,
    IBackupEventPublisher eventPublisher,
    TelemetryProvider telemetry
) : IBackupRunService
{
    public async Task<BackupRunStartResult> StartBackupRunAsync(Guid deviceId, string? clientIp = null, CancellationToken cancellationToken = default)
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

            // Create SaS URLs for upload with optional IP restriction
            var devicePath = $"staging/{deviceId:N}/{runId:N}/";
            var uploadSas = await sasUrlService.GenerateUploadSasUrlAsync(devicePath, clientIp, ttlMinutes: 60, cancellationToken);

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

    public async Task<CommitJob> CommitBackupRunAsync(Guid deviceId, Guid runId, string? manifestPath = null, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("CommitBackupRun");
        activity?.SetTag(ActivityAttributes.OperationName, "CommitBackupRun");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var stopwatch = Stopwatch.StartNew();
        var metricTags = new TagList { { "operation", "commit_run" } };

        try
        {
            // Verify the backup run exists and is in valid state
            var run = await manifestManager.GetBackupRunAsync(deviceId, runId, cancellationToken);
            
            if (run.Status == BackupRunStatus.Succeeded)
            {
                throw new InvalidOperationException($"Backup run {runId} has already been committed");
            }

            // Default manifest path if not provided
            manifestPath ??= $"runs/{deviceId:N}/{runId:N}/run-manifest.json";

            // Create a CommitJob for async processing
            var commitJob = await manifestManager.CreateCommitJobAsync(deviceId, runId, cancellationToken);
            activity?.SetTag("commit_id", commitJob.CommitId.ToString());

            stopwatch.Stop();
            telemetry.BackupRunsCommitted.Add(1, metricTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag("commit_job_status", commitJob.Status.ToString());

            // Publish event for async post-processing
            await eventPublisher.PublishBackupRunCommittedAsync(commitJob, manifestPath, cancellationToken);

            return commitJob;
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

    public async Task<CommitJob> GetCommitStatusAsync(Guid commitId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetCommitStatus");
        activity?.SetTag(ActivityAttributes.OperationName, "GetCommitStatus");
        activity?.SetTag("commit_id", commitId.ToString());

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var commitJob = await manifestManager.GetCommitJobAsync(commitId, cancellationToken);

            stopwatch.Stop();
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, 
                new TagList { { "operation", "get_commit_status" } });

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag("commit_job_status", commitJob.Status.ToString());

            return commitJob;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(ActivityAttributes.OperationStatus, "error");
            activity?.AddException(ex);

            throw;
        }
    }
}