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
    /// Gets the locally persisted in-flight backup run header, if one exists for the device.
    /// The returned header carries journal counts but not the journal contents.
    /// </summary>
    Task<PendingBackupRun?> GetPendingBackupRunAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the in-flight backup run header. Cheap to call after every batch: the row holds only
    /// run-level metadata, never the uploaded-file set.
    /// </summary>
    Task SavePendingBackupRunAsync(PendingBackupRun pendingRun, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the in-flight run header along with its journal and deletion rows, after the run is
    /// committed and local state is updated.
    /// </summary>
    Task ClearPendingBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a batch of successfully staged files to the in-flight run's journal. Idempotent per
    /// storage path, so retrying a batch does not duplicate entries.
    /// </summary>
    Task AppendPendingRunFilesAsync(
        Guid deviceId,
        Guid runId,
        IReadOnlyList<TaggedFile> files,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a file already staged for this run whose hash and size still match the freshly scanned
    /// copy, so its upload can be skipped on resume. Returns null when there is no usable match.
    /// </summary>
    Task<TaggedFile?> FindStagedRunFileAsync(
        Guid deviceId,
        Guid runId,
        string storagePath,
        string? sha256Hash,
        long sizeBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the run's journal in a stable order, one entry at a time.
    /// Holds the state store's single-writer lock for the whole enumeration, so enumerate it to
    /// completion (or dispose it) before starting any other state-store call, and never interleave
    /// it with another stream from this service.
    /// </summary>
    IAsyncEnumerable<TaggedFile> StreamPendingRunFilesAsync(
        Guid deviceId,
        Guid runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes the run's journal into tracked file state in a single set-based statement.
    /// </summary>
    Task PromotePendingRunFilesToStateAsync(
        Guid deviceId,
        Guid runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes server-rejected files from an in-flight journal before the remaining successful
    /// entries are promoted. The removed paths remain absent from tracked state and are therefore
    /// detected again by the next scan.
    /// </summary>
    Task RemovePendingRunFilesAsync(
        Guid deviceId,
        Guid runId,
        IReadOnlyList<string> storagePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes paths that an older client incorrectly promoted even though the server rejected them.
    /// The next scan consequently treats them as new files.
    /// </summary>
    Task RemoveTrackedFilesAsync(
        IReadOnlyList<string> storagePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a scan, discarding any scanned-path scratch data from a previous scan.
    /// </summary>
    Task BeginScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a batch of scanned storage paths for this scan. Used by deletion detection.
    /// </summary>
    Task AppendScannedPathsAsync(IReadOnlyList<string> storagePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the tracked files the current scan did not see, without needing a run to attribute
    /// them to. Evaluated inside SQLite; nothing is materialized in memory.
    /// </summary>
    Task<int> CountScanDeletionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records this run's deletions as every tracked file the current scan did not see, replacing
    /// anything a previous attempt at the same run recorded, and returns how many there were.
    /// </summary>
    Task<int> RecordScanDeletionsAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the deletions recorded for this run, one path at a time. Same locking constraint as
    /// <see cref="StreamPendingRunFilesAsync"/>.
    /// </summary>
    IAsyncEnumerable<string> StreamPendingRunDeletionsAsync(
        Guid deviceId,
        Guid runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops tracked file state for everything this run recorded as deleted, in one statement.
    /// </summary>
    Task ApplyPendingRunDeletionsAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);
}
