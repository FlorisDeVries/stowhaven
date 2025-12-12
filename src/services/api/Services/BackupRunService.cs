using FlorisDeV.BackupApi.Models.Application;
using FlorisDeV.BackupApi.Models.State;

namespace FlorisDeV.BackupApi.Services;

public interface IBackupRunService
{
    Task<BackupRunStartResult> StartBackupRunAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);
}

public class BackupRunService(
    IManifestManager manifestManager,
    ISasUrlService sasUrlService,
    ILogger<BackupRunService> logger
) : IBackupRunService
{
    public async Task<BackupRunStartResult> StartBackupRunAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        // Start Run
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var run = await manifestManager.CreateBackupRunAsync(deviceId, runId, startedAt, cancellationToken);

        // Create SaS URLs for upload
        var devicePath = $"staging/{deviceId:N}/{runId:N}/";
        var uploadSas = await sasUrlService.GenerateUploadSasUrlAsync(devicePath, ttlMinutes: 60, cancellationToken);

        var runStartDto = new BackupRunStartResult
        {
            Run = run,
            SasUrl = uploadSas
        };

        // Return Run info + SaS URLs
        return runStartDto;
    }

    public async Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        // Commit Run in Manifest
        var run = await manifestManager.CommitBackupRunAsync(deviceId, runId, cancellationToken);

        // Queue worker for async post-processing (not implemented yet)

        return run;
    }
}