using FlorisDeV.BackupClient.Models;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Extension methods for BackupStateService to work with multi-target backups.
/// </summary>
public static class BackupStateServiceExtensions
{
    /// <summary>
    /// Upserts tagged file states (files with target name prefix in storage path).
    /// </summary>
    public static async Task UpsertTaggedFileStateBatchAsync(
        this IBackupStateService service,
        IReadOnlyList<TaggedFile> taggedFiles,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        // Convert tagged files to storage paths
        var filesWithStoragePaths = taggedFiles
            .Select(tf => (StoragePath: tf.GetStoragePath(), File: tf.Metadata))
            .ToList();

        // Use interface method
        await service.UpsertFileStateBatchAsync(filesWithStoragePaths, runId, cancellationToken);
    }
}
