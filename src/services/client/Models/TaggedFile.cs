using FlorisDeV.BackupClient.Services;

namespace FlorisDeV.BackupClient.Models;

/// <summary>
/// Associates a file with its backup target for multi-directory support.
/// </summary>
public record TaggedFile(string TargetName, string TargetDirectory, FileMetadata Metadata)
{
    /// <summary>
    /// Gets the storage path: "{targetName}/{relativePath}"
    /// This ensures unique paths across multiple backup targets.
    /// </summary>
    public string GetStoragePath()
    {
        var relativePath = Path.GetRelativePath(TargetDirectory, Metadata.FilePath);
        // Normalize to forward slashes and prepend target name
        var normalizedRelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        return $"{TargetName}/{normalizedRelativePath}";
    }
}
