using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.Manifest;

namespace FlorisDeV.BackupContracts.Api.Responses;

public sealed record RestoreFileItem
{
    public required string LogicalPath { get; init; }
    public required string UniqueFileId { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
    public FileEncryptionMetadata? Encryption { get; init; }
}

public sealed record ListRestoreFilesResponse
{
    public required Guid DeviceId { get; init; }
    public required IReadOnlyList<RestoreFileItem> Files { get; init; }
    public required int PageSize { get; init; }
    public string? ContinuationToken { get; init; }
    public string? NextContinuationToken { get; init; }
    public bool HasMore => !string.IsNullOrWhiteSpace(NextContinuationToken);
}

public sealed record StartRestoreResponse
{
    public required Guid RestoreId { get; init; }
    public required Guid DeviceId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required SasUrlInfo SasUrlInfo { get; init; }
    public required IReadOnlyList<RestoreFileItem> Files { get; init; }
}
