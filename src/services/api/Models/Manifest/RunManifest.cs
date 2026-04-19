namespace FlorisDeV.BackupApi.Models.Manifest;

/// <summary>
/// Represents the run-manifest.json file uploaded by the client after completing file uploads.
/// This manifest describes all file changes in a backup run.
/// </summary>
public sealed record RunManifest
{
    /// <summary>
    /// The device identifier.
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>
    /// The backup run identifier.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// List of new or changed files in this run.
    /// </summary>
    public required List<ManifestFileEntry> Files { get; init; }

    /// <summary>
    /// List of relative paths of files that were deleted in this run.
    /// </summary>
    public required List<string> Deleted { get; init; }
}

/// <summary>
/// Represents a single file entry in the run manifest.
/// </summary>
public sealed record ManifestFileEntry
{
    /// <summary>
    /// Relative path of the file on the client device.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// Unique file identifier: {sha256}_{timestamp}_{random}
    /// </summary>
    public required string UniqueFileId { get; init; }

    /// <summary>
    /// SHA-256 hash of the file content.
    /// </summary>
    public required string Sha256 { get; init; }

    /// <summary>
    /// Size of the file in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Last modification time of the file.
    /// </summary>
    public required DateTimeOffset Mtime { get; init; }
}
