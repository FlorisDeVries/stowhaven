using FlorisDeV.BackupClient.Services;

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
}
