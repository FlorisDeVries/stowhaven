using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupContracts.Manifest;

namespace FlorisDeV.BackupClient.Models;

/// <summary>
/// Associates a file with its backup target for multi-directory support.
/// </summary>
public record TaggedFile(string TargetName, string TargetDirectory, FileMetadata Metadata)
{
    /// <summary>
    /// Unique physical blob identifier used for staging and committed blob names.
    /// The logical path remains TargetName + relative path.
    /// </summary>
    public string? UniqueFileId { get; init; }

    /// <summary>
    /// SHA-256 hash of the exact bytes uploaded to blob storage. This differs from Metadata.Hash when client-side encryption is enabled.
    /// </summary>
    public string? UploadSha256 { get; init; }

    /// <summary>
    /// Size of the exact bytes uploaded to blob storage. This differs from Metadata.SizeBytes when client-side encryption is enabled.
    /// </summary>
    public long? UploadSizeBytes { get; init; }

    /// <summary>
    /// Optional client-side encryption metadata required to decrypt this uploaded file during restore.
    /// </summary>
    public FileEncryptionMetadata? Encryption { get; init; }

    /// <summary>
    /// Gets the relative path within the backup target using forward slashes.
    /// </summary>
    public string GetRelativePath()
    {
        var relativePath = Path.GetRelativePath(TargetDirectory, Metadata.FilePath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Gets the storage path: "{targetName}/{relativePath}"
    /// This ensures unique paths across multiple backup targets.
    /// </summary>
    public string GetStoragePath()
    {
        return $"{TargetName}/{GetRelativePath()}";
    }

    public string GetUploadSha256()
        => UploadSha256 ?? Metadata.Hash ?? throw new InvalidOperationException($"Missing upload SHA-256 for {GetStoragePath()}");

    public long GetUploadSizeBytes() => UploadSizeBytes ?? Metadata.SizeBytes;
}
