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
    DaprClient daprClient
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
        return state;
    }

    public Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var stateKey = $"{deviceId}/backupruns/{runId}";
        return daprClient.GetStateAsync<BackupRun>(DaprComponents.ManifestStateStore, stateKey,
            cancellationToken: cancellationToken);
    }

    public async Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var stateKey = $"{deviceId}/backupruns/{runId}";
        var run = await daprClient.GetStateAsync<BackupRun>(DaprComponents.ManifestStateStore, stateKey,
            cancellationToken: cancellationToken);

        if (run == null)
        {
            throw new BackupRunNotFoundException(deviceId, runId);
        }

        run.Status = BackupRunStatus.Succeeded;
        run.CompletedAt = DateTimeOffset.UtcNow;

        await daprClient.SaveStateAsync(DaprComponents.ManifestStateStore, stateKey, run,
            cancellationToken: cancellationToken);
        return run;
    }
}