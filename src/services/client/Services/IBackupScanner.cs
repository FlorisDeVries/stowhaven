using FlorisDeV.BackupClient.Models;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Handles file scanning and smart hashing for backup operations.
/// Optimizes I/O by only computing hashes when necessary.
/// </summary>
public interface IBackupScanner
{
    /// <summary>
    /// Scans all configured backup targets and yields files with their target metadata.
    /// </summary>
    IAsyncEnumerable<TaggedFile> ScanAllTargetsAsync(
        IReadOnlyDictionary<string, string> targets,
        string[]? excludePatterns,
        CancellationToken cancellationToken);

    /// <summary>
    /// Performs smart hashing for a tagged file - only computes hash if needed.
    /// Returns the file with hash populated and a flag indicating if it needs backup.
    /// </summary>
    Task<(TaggedFile File, bool NeedsBackup, FileChangeType ChangeType)> AnalyzeFileAsync(
        TaggedFile taggedFile,
        CancellationToken cancellationToken);
}
