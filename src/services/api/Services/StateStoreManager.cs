using System.Diagnostics;
using Dapr.Client;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Models.State;
using FlorisDeV.BackupApi.Telemetry;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupApi.Services;

public interface IManifestManager
{
    Task<BackupRun> CreateBackupRunAsync(Guid deviceId, Guid runId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);
}

public partial class ManifestManager(
    DaprClient daprClient,
    ILogger<ManifestManager> logger,
    TelemetryProvider telemetry
) : IManifestManager
{
    public async Task<BackupRun> CreateBackupRunAsync(Guid deviceId, Guid runId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("CreateBackupRun");
        var stateKey = $"{deviceId}/backupruns/{runId}";

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var state = new BackupRun
        {
            RunId = runId,
            DeviceId = deviceId,
            StartedAt = startedAt,
            Status = BackupRunStatus.Queued,
        };

        await daprClient.SaveStateAsync(DaprComponents.ManifestStateStore, stateKey, state,
            cancellationToken: cancellationToken);

        telemetry.StateOperations.Add(1, new TagList { { "operation", "create" }, { "store", "manifest" } });
        
        LogBackupRunCreated(logger, runId, deviceId);
        return state;
    }

    public async Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetBackupRun");
        var stateKey = $"{deviceId}/backupruns/{runId}";

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var (run, etag) = await daprClient.GetStateAndETagAsync<BackupRun>(
            DaprComponents.ManifestStateStore, 
            stateKey,
            cancellationToken: cancellationToken);
        
        if (run != null)
        {
            // Store the ETag in the model for later use
            run.ETag = etag;
            activity?.SetTag(ActivityAttributes.StateETag, etag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "result", "found" } });
            return run;
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "result", "not_found" } });
        throw new BackupRunNotFoundException(deviceId, runId);
    }

    public async Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var stateKey = $"{deviceId}/backupruns/{runId}";
        
        // Get the current state with ETag
        var (run, etag) = await daprClient.GetStateAndETagAsync<BackupRun>(
            DaprComponents.ManifestStateStore, 
            stateKey,
            cancellationToken: cancellationToken);

        if (run == null)
        {
            throw new BackupRunNotFoundException(deviceId, runId);
        }

        // Validate state transition
        if (run.Status == BackupRunStatus.Succeeded)
        {
            throw new BackupRunAlreadyCommittedException(deviceId, runId);
        }

        if (run.Status == BackupRunStatus.Failed)
        {
            throw new InvalidBackupRunStateException(
                deviceId, 
                runId, 
                run.Status, 
                BackupRunStatus.Queued);
        }

        // Update the run status
        run.Status = BackupRunStatus.Succeeded;
        run.CompletedAt = DateTimeOffset.UtcNow;

        // Attempt to save with ETag for optimistic concurrency control
        var success = await daprClient.TrySaveStateAsync(
            DaprComponents.ManifestStateStore, 
            stateKey, 
            run,
            etag,
            cancellationToken: cancellationToken);

        if (!success)
        {
            // ETag mismatch - concurrent update detected
            LogConcurrentUpdateDetected(logger, runId, deviceId, etag);
            
            throw new ConcurrentUpdateException(deviceId, runId, etag, actualETag: null);
        }

        LogBackupRunCommitted(logger, runId, deviceId, run.Status);

        return run;
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Created backup run {runId} for device {deviceId}")]
    static partial void LogBackupRunCreated(ILogger logger, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for backup run {runId} of device {deviceId}. ETag: {etag}")]
    static partial void LogConcurrentUpdateDetected(ILogger logger, Guid runId, Guid deviceId, string etag);

    [LoggerMessage(LogLevel.Information, "Committed backup run {runId} for device {deviceId} with status {status}")]
    static partial void LogBackupRunCommitted(ILogger logger, Guid runId, Guid deviceId, BackupRunStatus status);

    #endregion
}