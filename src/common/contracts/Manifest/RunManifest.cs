namespace FlorisDeV.BackupContracts.Manifest;

/// <summary>
/// Represents the run-manifest.json file uploaded by the client after completing file uploads.
/// This manifest describes all file changes in a backup run.
/// </summary>
public sealed record RunManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string DeviceId { get; init; }
    public required string RunId { get; init; }
    public required List<ManifestFileEntry> Files { get; init; }
    public required List<string> Deleted { get; init; }
}

public sealed record ManifestFileEntry
{
    public string? TargetName { get; init; }
    public required string RelativePath { get; init; }

    public string LogicalPath => string.IsNullOrWhiteSpace(TargetName)
        ? RelativePath
        : $"{TargetName}/{RelativePath}";

    public required string UniqueFileId { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset Mtime { get; init; }
    public FileEncryptionMetadata? Encryption { get; init; }
}

public sealed record FileEncryptionMetadata
{
    public required string Mode { get; init; }
    public required string Algorithm { get; init; }
    public required string KeyWrapAlgorithm { get; init; }
    public required string Kdf { get; init; }
    public required int KdfIterations { get; init; }
    public required string KdfSalt { get; init; }
    public required string Iv { get; init; }
    public required string WrappedKey { get; init; }
    public required string AuthenticationTag { get; init; }
    public string? PlaintextSha256 { get; init; }
    public long? PlaintextSize { get; init; }
}