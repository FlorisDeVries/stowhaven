using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupContracts.Api.Responses;

public sealed record ListDevicesResponse
{
    public required string TenantId { get; init; }
    public required IReadOnlyList<DeviceRegistrationResponse> Devices { get; init; }
    public required int PageSize { get; init; }
    public string? NextContinuationToken { get; init; }
}

public sealed record BackupUserResponse
{
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
}

public sealed record ListBackupUsersResponse
{
    public required string TenantId { get; init; }
    public required IReadOnlyList<BackupUserResponse> Users { get; init; }
    public required int PageSize { get; init; }
    public string? NextContinuationToken { get; init; }
}

public sealed record DeviceStateResponse
{
    public required DeviceRegistrationResponse Device { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public ManifestSummaryResponse? LatestRun { get; init; }
    public ManifestSummaryResponse? LatestSuccessfulRun { get; init; }
    public ManifestSummaryResponse? LatestRunWithErrors { get; init; }
    public CommitStatusResponse? LatestCommit { get; init; }
    public DeviceCommitStateResponse? CommitState { get; init; }
}

public sealed record DeviceCommitStateResponse
{
    public required Guid CommitId { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required bool IsHeld { get; init; }
    public Guid? LatestCompletedRunId { get; init; }
    public DateTimeOffset? LatestCompletedRunStartedAt { get; init; }
}

public sealed record DeviceFileResponse
{
    public required string LogicalPath { get; init; }
    public required string CurrentVersionId { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
    public required string LastBackupRunId { get; init; }
    public required bool IsDeleted { get; init; }
}

public sealed record ListDeviceFilesResponse
{
    public required Guid DeviceId { get; init; }
    public required IReadOnlyList<DeviceFileResponse> Files { get; init; }
    public required int PageSize { get; init; }
    public string? NextContinuationToken { get; init; }
}

public sealed record DeviceFileDetailsResponse
{
    public required Guid DeviceId { get; init; }
    public required DeviceFileResponse Entry { get; init; }
    public FileVersionState? VersionState { get; init; }
    public string? Sha256 { get; init; }
    public string? EncryptionMode { get; init; }
    public DateTimeOffset? VersionCreatedAt { get; init; }
    public DateTimeOffset? VersionRetiredAt { get; init; }
    public required bool VersionAvailable { get; init; }
}
