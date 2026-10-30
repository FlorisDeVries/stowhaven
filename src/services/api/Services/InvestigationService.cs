using System.Text.Json;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupApi.Services;

public interface IInvestigationService
{
    Task<ListDevicesResponse> ListDevicesAsync(string tenantId, string? userId, DeviceRegistrationStatus? status,
        Guid? deviceId, int pageSize, string? continuationToken, CancellationToken cancellationToken);
    Task<ListBackupUsersResponse> ListUsersAsync(string tenantId, int pageSize, string? continuationToken, CancellationToken cancellationToken);
    Task<DeviceRegistrationResponse> GetDeviceAsync(string tenantId, Guid deviceId, CancellationToken cancellationToken);
    Task<DeviceStateResponse> GetDeviceStateAsync(string tenantId, Guid deviceId, CancellationToken cancellationToken);
    Task<ListDeviceFilesResponse> ListFilesAsync(string tenantId, Guid deviceId, int pageSize, string? continuationToken, CancellationToken cancellationToken);
    Task<DeviceFileDetailsResponse?> GetFileAsync(string tenantId, Guid deviceId, string logicalPath, CancellationToken cancellationToken);
}

public sealed class InvestigationService(
    [FromKeyedServices(StateStores.DeviceRegistry)] IStateDocumentStore registryStore,
    [FromKeyedServices(StateStores.Manifest)] IStateDocumentStore manifestStore,
    IDeviceRegistryService registry,
    IManifestManager manifests) : IInvestigationService
{
    public async Task<ListDevicesResponse> ListDevicesAsync(string tenantId, string? userId, DeviceRegistrationStatus? status,
        Guid? deviceId, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        ValidatePageSize(pageSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (status.HasValue && !Enum.IsDefined(status.Value)) throw new ArgumentException("Invalid registration status.", nameof(status));
        if (userId?.Length > 256) throw new ArgumentException("User ID is too long.", nameof(userId));
        var filters = new List<DocumentFieldEquals> { new("tenantId", tenantId) };
        if (!string.IsNullOrWhiteSpace(userId)) filters.Add(new("userId", userId));
        if (status.HasValue) filters.Add(new("status", JsonNamingPolicy.CamelCase.ConvertName(status.Value.ToString())));
        if (deviceId.HasValue) filters.Add(new("deviceId", deviceId.Value.ToString("D")));
        var result = await registryStore.QueryAsync<DeviceRegistration>(new DocumentQuery
        {
            Type = "device", FieldEquals = filters, PageSize = pageSize, ContinuationToken = continuationToken
        }, cancellationToken);
        return new ListDevicesResponse
        {
            TenantId = tenantId, Devices = result.Items.Select(item => ToDevice(item.Data)).ToArray(),
            PageSize = pageSize, NextContinuationToken = result.NextContinuationToken
        };
    }

    public async Task<ListBackupUsersResponse> ListUsersAsync(string tenantId, int pageSize, string? continuationToken,
        CancellationToken cancellationToken)
    {
        ValidatePageSize(pageSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        // Query the existing registry directly, so historical registrations appear
        // immediately. Ordered DISTINCT keeps owners unique across page boundaries.
        var result = await registryStore.QueryDistinctValuesAsync(new DocumentQuery
        {
            Type = "device", FieldEquals = [new("tenantId", tenantId)],
            PageSize = pageSize, ContinuationToken = continuationToken
        }, "userId", cancellationToken);
        return new ListBackupUsersResponse
        {
            TenantId = tenantId, Users = result.Values.Select(id => new BackupUserResponse { TenantId = tenantId, UserId = id }).ToArray(),
            PageSize = pageSize, NextContinuationToken = result.NextContinuationToken
        };
    }

    public async Task<DeviceRegistrationResponse> GetDeviceAsync(string tenantId, Guid deviceId, CancellationToken cancellationToken)
        => ToDevice(await GetRegistrationAsync(tenantId, deviceId, cancellationToken));

    public async Task<DeviceStateResponse> GetDeviceStateAsync(string tenantId, Guid deviceId, CancellationToken cancellationToken)
    {
        var registration = await GetRegistrationAsync(tenantId, deviceId, cancellationToken);
        var partition = $"device:{deviceId:N}";
        var latest = LatestRunAsync(partition, null, cancellationToken);
        var successful = LatestRunAsync(partition, BackupRunStatus.Succeeded, cancellationToken);
        var partial = LatestRunAsync(partition, BackupRunStatus.CompletedWithErrors, cancellationToken);
        var commit = manifests.GetCommitJobsPageAsync(new CommitJobQuery { DeviceId = deviceId, PageSize = 1 }, cancellationToken);
        var lease = manifestStore.GetAsync<DeviceCommitLease>("deviceCommitLease", partition, "commit", cancellationToken);
        await Task.WhenAll(latest, successful, partial, commit, lease);
        var observedAt = DateTimeOffset.UtcNow;
        var coordination = (await lease)?.Data;
        return new DeviceStateResponse
        {
            Device = ToDevice(registration), RevokedAt = registration.RevokedAt, ObservedAt = observedAt,
            LatestRun = ToRun(await latest), LatestSuccessfulRun = ToRun(await successful), LatestRunWithErrors = ToRun(await partial),
            LatestCommit = (await commit).Commits.FirstOrDefault() is { } job ? ToCommit(job) : null,
            CommitState = coordination is null ? null : new DeviceCommitStateResponse
            {
                CommitId = coordination.CommitId, ExpiresAt = coordination.ExpiresAt,
                IsHeld = coordination.ClaimId is not null && coordination.ExpiresAt > observedAt,
                LatestCompletedRunId = coordination.LatestRunId,
                LatestCompletedRunStartedAt = coordination.LatestRunStartedAt
            }
        };
    }

    public async Task<ListDeviceFilesResponse> ListFilesAsync(string tenantId, Guid deviceId, int pageSize,
        string? continuationToken, CancellationToken cancellationToken)
    {
        ValidatePageSize(pageSize);
        await GetRegistrationAsync(tenantId, deviceId, cancellationToken);
        var result = await manifests.GetFileEntriesPageAsync(deviceId, pageSize, continuationToken, cancellationToken);
        return new ListDeviceFilesResponse
        {
            DeviceId = deviceId, Files = result.Entries.Select(ToFile).ToArray(),
            PageSize = pageSize, NextContinuationToken = result.NextContinuationToken
        };
    }

    public async Task<DeviceFileDetailsResponse?> GetFileAsync(string tenantId, Guid deviceId, string logicalPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        if (logicalPath.Length > 4096) throw new ArgumentException("Logical path is too long.", nameof(logicalPath));
        await GetRegistrationAsync(tenantId, deviceId, cancellationToken);
        var entry = await manifests.GetFileEntryAsync(deviceId, logicalPath, cancellationToken);
        if (entry is null) return null;
        var version = string.IsNullOrWhiteSpace(entry.CurrentVersionId) ? null
            : await manifests.GetFileVersionAsync(deviceId, entry.CurrentVersionId, cancellationToken);
        return new DeviceFileDetailsResponse
        {
            DeviceId = deviceId, Entry = ToFile(entry), VersionAvailable = version is not null,
            VersionState = version?.State, Sha256 = version?.Sha256, EncryptionMode = version?.Encryption?.Mode,
            VersionCreatedAt = version?.CreatedAt, VersionRetiredAt = version?.RetiredAt
        };
    }

    private async Task<DeviceRegistration> GetRegistrationAsync(string tenantId, Guid deviceId, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var device = await registry.GetDeviceAsync(deviceId, token);
        if (!string.Equals(device.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            throw new DeviceNotRegisteredException(deviceId);
        return device;
    }

    private async Task<BackupRun?> LatestRunAsync(string partition, BackupRunStatus? status, CancellationToken token)
    {
        // Filter in storage before taking one row. The historical listing endpoint
        // filters statuses after pagination and cannot answer this query reliably.
        var result = await manifestStore.QueryAsync<BackupRun>(new DocumentQuery
        {
            Type = "backupRun", PartitionKey = partition, PageSize = 1, Order = DocumentOrder.SortValueDescending,
            FieldEquals = status.HasValue ? [new("status", JsonNamingPolicy.CamelCase.ConvertName(status.Value.ToString()))] : []
        }, token);
        return result.Items.FirstOrDefault()?.Data;
    }

    private static void ValidatePageSize(int value)
    {
        if (value is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(value), "Page size must be between 1 and 100.");
    }

    private static DeviceRegistrationResponse ToDevice(DeviceRegistration device) => new()
    {
        DeviceId = device.DeviceId, TenantId = device.TenantId, UserId = device.UserId,
        DisplayName = device.DisplayName, Status = device.Status, CreatedAt = device.CreatedAt, LastSeenAt = device.LastSeenAt
    };

    private static ManifestSummaryResponse? ToRun(BackupRun? run) => run is null ? null : new()
    {
        DeviceId = run.DeviceId, RunId = run.RunId, Status = run.Status,
        StartedAt = run.StartedAt, CompletedAt = run.CompletedAt, FilesBackedUp = run.FilesBackedUp
    };

    private static CommitStatusResponse ToCommit(CommitJob job) => new()
    {
        CommitId = job.CommitId, DeviceId = job.DeviceId, RunId = job.RunId, Status = job.Status,
        CreatedAt = job.CreatedAt, UpdatedAt = job.UpdatedAt, CompletedAt = job.CompletedAt,
        TotalFiles = job.TotalFiles, FilesProcessed = job.FilesProcessed, FilesFailed = job.FilesFailed,
        Error = job.Error, FailureCategory = job.FailureCategory, AttemptCount = job.AttemptCount,
        LastErrorAt = job.LastErrorAt, NextRetryAt = job.NextRetryAt, DeadLetteredAt = job.DeadLetteredAt
    };

    private static DeviceFileResponse ToFile(FileEntry entry) => new()
    {
        LogicalPath = entry.RelativePath, CurrentVersionId = entry.CurrentVersionId, Size = entry.Size,
        LastWriteUtc = entry.LastWriteUtc, LastBackupRunId = entry.LastBackupRunId, IsDeleted = entry.IsDeleted
    };
}
