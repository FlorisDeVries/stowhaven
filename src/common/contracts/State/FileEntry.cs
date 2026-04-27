using FlorisDeV.BackupContracts.Manifest;

namespace FlorisDeV.BackupContracts.State;

/// <summary>
/// Represents the latest active file mapping per path for a device.
/// Key: (deviceId, relativePath)
/// </summary>
public sealed record FileEntry
{
    public required Guid DeviceId { get; init; }
    public required string RelativePath { get; init; }
    public required string CurrentVersionId { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
    public required string LastBackupRunId { get; init; }
    public bool IsDeleted { get; init; }
    public string? ETag { get; set; }
}

/// <summary>
/// Represents a specific version of a file (active or retired).
/// Key: (deviceId, uniqueFileId)
/// </summary>
public sealed record FileVersion
{
    public required Guid DeviceId { get; init; }
    public required string UniqueFileId { get; init; }
    public required string RelativePath { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public FileEncryptionMetadata? Encryption { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RetiredAt { get; init; }
    public required FileVersionState State { get; init; }
    public string? ETag { get; set; }
}

public enum FileVersionState
{
    Active,
    Retired
}

public sealed record FileEntryIndex
{
    public required Guid DeviceId { get; init; }
    public required List<string> RelativePaths { get; init; }
    public string? ETag { get; set; }
}

public sealed record FileEntryPage
{
    public required IReadOnlyList<FileEntry> Entries { get; init; }
    public required int PageSize { get; init; }
    public string? ContinuationToken { get; init; }
    public string? NextContinuationToken { get; init; }
    public bool HasMore => !string.IsNullOrWhiteSpace(NextContinuationToken);
}