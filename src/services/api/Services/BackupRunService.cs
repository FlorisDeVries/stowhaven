using FlorisDeV.BackupApi.Models.StateStore;

namespace FlorisDeV.BackupApi.Services;

public interface IBackupRunService
{
    Task<BackupRunDto> StartBackupRunAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<BackupRunDto> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);
}

public class BackupRunService(
    IManifestManager manifestManager,
    ILogger<BackupRunService> logger
) : IBackupRunService
{
    public async Task<BackupRunDto> StartBackupRunAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        // Start Run
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var run = await manifestManager.CreateBackupRunAsync(deviceId, runId, startedAt, cancellationToken);

        // Create SaS URLs for upload (not implemented yet)

        // Return Run info + SaS URLs

        return run;
    }

    public async Task<BackupRunDto> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        // Commit Run in Manifest
        var run = await manifestManager.CommitBackupRunAsync(deviceId, runId, cancellationToken);

        // Queue worker for async post-processing (not implemented yet)

        return run;
    }
}