namespace FlorisDeV.BackupApi.Models.State;

/// <summary>
/// Represents the latest active file mapping per path for a device.
/// Key: (deviceId, relativePath)
/// </summary>
public sealed record FileEntry
{
    /// <summary>
    /// Device identifier.
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>
    /// Relative path of the file on the device.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// Current active version's unique file ID.
    /// </summary>
    public required string CurrentVersionId { get; init; }

    /// <summary>
    /// Size of the current version in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Last write time of the current version.
    /// </summary>
    public required DateTimeOffset LastWriteUtc { get; init; }

    /// <summary>
    /// Backup run ID that last modified this file.
    /// </summary>
    public required string LastBackupRunId { get; init; }

    /// <summary>
    /// Whether this file is marked as deleted.
    /// </summary>
    public bool IsDeleted { get; init; }

    /// <summary>
    /// ETag for optimistic concurrency control.
    /// </summary>
    public string? ETag { get; set; }
}

/// <summary>
/// Represents a specific version of a file (active or retired).
/// Key: (deviceId, uniqueFileId)
/// </summary>
public sealed record FileVersion
{
    /// <summary>
    /// Device identifier.
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>
    /// Unique file identifier: {sha256}_{timestamp}_{random}
    /// </summary>
    public required string UniqueFileId { get; init; }

    /// <summary>
    /// Relative path of the file on the device.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// SHA-256 hash of the file content.
    /// </summary>
    public required string Sha256 { get; init; }

    /// <summary>
    /// Size of the file in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// When this version was created (uploaded).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When this version was retired (null if still active).
    /// </summary>
    public DateTimeOffset? RetiredAt { get; init; }

    /// <summary>
    /// State of this version.
    /// </summary>
    public required FileVersionState State { get; init; }

    /// <summary>
    /// ETag for optimistic concurrency control.
    /// </summary>
    public string? ETag { get; set; }
}

/// <summary>
/// State of a file version.
/// </summary>
public enum FileVersionState
{
    /// <summary>
    /// Active version currently mapped to a path.
    /// </summary>
    Active,

    /// <summary>
    /// Retired version (superseded or deleted).
    /// </summary>
    Retired
}
