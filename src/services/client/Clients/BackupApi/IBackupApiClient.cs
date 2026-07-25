using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using Refit;

namespace FlorisDeV.BackupClient.Clients.BackupApi;

public interface IBackupApiClient
{
    /// <summary>
    /// Anonymous liveness endpoint used to wake up a scaled-to-zero API/gateway before real traffic is sent.
    /// </summary>
    [Get("/api/health/alive")]
    Task<HttpResponseMessage> Ping(CancellationToken cancellationToken = default);

    [Post("/api/devices")]
    Task<DeviceRegistrationResponse> RegisterDevice(RegisterDeviceRequest request,
        CancellationToken cancellationToken = default);

    [Post("/api/devices/{deviceId}/backup/start-run")]
    Task<StartBackupRunResponse> StartBackupRun(Guid deviceId,
        CancellationToken cancellationToken = default);

    [Post("/api/devices/{deviceId}/backup/runs/{runId}/refresh-sas")]
    Task<RefreshSasUrlResponse> RefreshBackupRunSas(Guid deviceId, Guid runId,
        CancellationToken cancellationToken = default);

    [Post("/api/devices/{deviceId}/backup/commit-run")]
    Task<CommitBackupRunResponse> CommitBackupRun(Guid deviceId, CommitBackupRunRequest request,
        CancellationToken cancellationToken = default);

    [Get("/api/devices/{deviceId}/backup/commit-status/{commitId}")]
    Task<CommitStatusResponse> GetCommitStatus(Guid deviceId, Guid commitId,
        CancellationToken cancellationToken = default);

    [Get("/api/devices/{deviceId}/restore/files")]
    Task<ListRestoreFilesResponse> ListRestoreFiles(Guid deviceId, [Query] int pageSize = 100,
        [Query] string? continuationToken = null,
        CancellationToken cancellationToken = default);

    [Post("/api/devices/{deviceId}/restore/start")]
    Task<StartRestoreResponse> StartRestore(Guid deviceId, StartRestoreRequest request,
        CancellationToken cancellationToken = default);
}