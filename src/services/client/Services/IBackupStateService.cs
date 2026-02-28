using FlorisDeV.BackupClient.Models;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Manages local backup state persistence using SQLite for delta detection.
/// Tracks which files have been backed up and their state at the time of backup.
/// </summary>
public interface IBackupStateService
{
    /// <summary>
    /// Gets or creates the device state record. Initializes database if needed.
    /// </summary>
    Task<DeviceState> GetOrCreateDeviceStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists backup success state after a successful backup run.
    /// Updates device state and file tracking records.
    /// </summary>
    Task SaveBackupSuccessAsync(
        Guid runId,
        string commitId,
        IReadOnlyList<FileMetadata> backedUpFiles,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the state of a specific file from the last backup.
    /// </summary>
    Task<BackupFileState?> GetFileStateAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all tracked file states from the last backup.
    /// </summary>
    Task<IReadOnlyList<BackupFileState>> GetAllFileStatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes file state records for deleted files.
    /// Called after successful backup to clean up deleted file tracking.
    /// </summary>
    Task RemoveDeletedFilesAsync(IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts multiple tagged file states in a single transaction.
    /// Storage paths are computed from the tagged files (includes target name prefix).
    /// </summary>
    /// <param name="taggedFiles">Tagged files to upsert.</param>
    /// <param name="runId">The backup run ID for these files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertFileStateBatchAsync(
        IReadOnlyList<TaggedFile> taggedFiles,
        Guid runId,
        CancellationToken cancellationToken = default);
}
