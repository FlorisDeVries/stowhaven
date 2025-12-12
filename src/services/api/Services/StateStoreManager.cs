using Dapr.Client;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Models.State;

namespace FlorisDeV.BackupApi.Services;

public interface IManifestManager
{
    Task<BackupRun> CreateBackupRunAsync(Guid deviceId, Guid runId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);
}

public class ManifestManager(
    DaprClient daprClient,
    ILogger<ManifestManager> logger
) : IManifestManager
{
    public async Task<BackupRun> CreateBackupRunAsync(Guid deviceId, Guid runId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        var stateKey = $"{deviceId}/backupruns/{runId}";
        var state = new BackupRun
        {
            RunId = runId,
            DeviceId = deviceId,
            StartedAt = startedAt,
            Status = BackupRunStatus.Queued,
        };

        await daprClient.SaveStateAsync(DaprComponents.ManifestStateStore, stateKey, state,
            cancellationToken: cancellationToken);
        
        logger.LogInformation("Created backup run {RunId} for device {DeviceId}", runId, deviceId);
        return state;
    }

    public async Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var stateKey = $"{deviceId}/backupruns/{runId}";
        var (run, etag) = await daprClient.GetStateAndETagAsync<BackupRun>(
            DaprComponents.ManifestStateStore, 
            stateKey,
            cancellationToken: cancellationToken);
        
        if (run != null)
        {
            // Store the ETag in the model for later use
            run.ETag = etag;
            return run;
        }
        
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
            logger.LogWarning(
                "Concurrent update detected for backup run {RunId} of device {DeviceId}. ETag: {ETag}",
                runId, deviceId, etag);
            
            throw new ConcurrentUpdateException(deviceId, runId, etag, actualETag: null);
        }

        logger.LogInformation(
            "Committed backup run {RunId} for device {DeviceId} with status {Status}",
            runId, deviceId, run.Status);

        return run;
    }
}