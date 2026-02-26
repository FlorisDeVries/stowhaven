using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Data;
using FlorisDeV.BackupClient.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    /// Compares current files against last backup to determine changes.
    /// Uses hash-based comparison for accuracy.
    /// </summary>
    /// <param name="currentFiles">Current filesystem state from FileSystemService</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Delta containing new, modified, and deleted files</returns>
    Task<BackupDelta> ComputeDeltaAsync(
        IReadOnlyList<FileMetadata> currentFiles,
        CancellationToken cancellationToken = default);

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
    /// Upserts multiple file states in a single transaction for efficient batch processing.
    /// </summary>
    /// <param name="files">Files to upsert into the backup state.</param>
    /// <param name="baseDirectory">The base directory to compute relative paths from.</param>
    /// <param name="runId">The backup run ID for these files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Use this method when processing files in batches to minimize database round-trips
    /// and improve performance for large file sets.
    /// </remarks>
    Task UpsertFileStateBatchAsync(
        IReadOnlyList<FileMetadata> files,
        string baseDirectory,
        Guid runId, 
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Helper extensions for BackupStateService to work with multi-target backups.
/// </summary>
internal static class BackupStateServiceExtensions
{
    /// <summary>
    /// Upserts tagged file states (files with target name prefix).
    /// </summary>
    public static async Task UpsertTaggedFileStateBatchAsync(
        this IBackupStateService service,
        IReadOnlyList<BackupService.TaggedFile> taggedFiles,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        // Convert tagged files to FileMetadata with storage paths
        var filesWithStoragePaths = taggedFiles
            .Select(tf => (StoragePath: tf.GetStoragePath(), File: tf.Metadata))
            .ToList();

        // Use internal method that stores with explicit relative paths
        await ((BackupStateService)service).UpsertFileStateBatchWithExplicitPathsAsync(
            filesWithStoragePaths, runId, cancellationToken);
    }
}

/// <summary>
/// SQLite-based implementation of backup state management.
/// </summary>
public partial class BackupStateService : IBackupStateService, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<BackupStateService> _logger;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _initialized;

    public BackupStateService(IOptions<DatabaseOptions> options, ILogger<BackupStateService> logger)
    {
        _logger = logger;

        var databasePath = options.Value.GetDatabasePath();

        // Ensure directory exists
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = $"Data Source={databasePath}";
    }

    public async Task<DeviceState> GetOrCreateDeviceStateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.SelectDeviceStateQuery, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (await reader.ReadAsync(cancellationToken))
            {
                var deviceState = new DeviceState(
                    DeviceId: Guid.Parse(reader.GetString(0)),
                    LastSuccessfulBackup: reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)),
                    LastRunId: reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                    LastCommitId: reader.IsDBNull(3) ? null : reader.GetString(3),
                    TotalFilesTracked: reader.GetInt64(4),
                    TotalBytesTracked: reader.GetInt64(5));

                LogDeviceStateLoaded(deviceState.DeviceId, deviceState.TotalFilesTracked);
                return deviceState;
            }

            // No device state exists - create new with hardware-based ID
            var newDeviceId = DeviceIdGenerator.GenerateDeviceId();
            var newState = new DeviceState(newDeviceId, null, null, null, 0, 0);

            await using var insertCommand = new SqliteCommand(BackupStateSql.InsertDeviceStateQuery, connection);
            insertCommand.Parameters.AddWithValue("@DeviceId", newDeviceId.ToString());
            insertCommand.Parameters.AddWithValue("@LastSuccessfulBackup", DBNull.Value);
            insertCommand.Parameters.AddWithValue("@LastRunId", DBNull.Value);
            insertCommand.Parameters.AddWithValue("@LastCommitId", DBNull.Value);
            insertCommand.Parameters.AddWithValue("@TotalFilesTracked", 0);
            insertCommand.Parameters.AddWithValue("@TotalBytesTracked", 0);

            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

            LogDeviceStateCreated(newDeviceId);
            return newState;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<BackupDelta> ComputeDeltaAsync(
        IReadOnlyList<FileMetadata> currentFiles,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Load all previously backed up files
            var previousFiles = new Dictionary<string, BackupFileState>(StringComparer.OrdinalIgnoreCase);

            await using var command = new SqliteCommand(BackupStateSql.SelectAllBackupFilesQuery, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var state = new BackupFileState(
                    RelativePath: reader.GetString(0),
                    Sha256Hash: reader.GetString(1),
                    SizeBytes: reader.GetInt64(2),
                    LastModifiedUtc: DateTimeOffset.Parse(reader.GetString(3)),
                    BackedUpAt: DateTimeOffset.Parse(reader.GetString(4)),
                    BackupRunId: Guid.Parse(reader.GetString(5)),
                    UniqueFileId: reader.IsDBNull(6) ? null : reader.GetString(6));

                previousFiles[state.RelativePath] = state;
            }

            // Compute delta
            var newFiles = new List<FileMetadata>();
            var modifiedFiles = new List<FileMetadata>();
            var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;

            foreach (var currentFile in currentFiles)
            {
                currentPaths.Add(currentFile.FilePath);

                if (!previousFiles.TryGetValue(currentFile.FilePath, out var previousFile))
                {
                    // New file - not in previous backup
                    newFiles.Add(currentFile);
                    totalBytes += currentFile.SizeBytes;
                }
                else
                {
                    // Check if file changed (size or hash)
                    var sizeChanged = currentFile.SizeBytes != previousFile.SizeBytes;
                    var hashChanged = currentFile.Hash != null &&
                                     currentFile.Hash != previousFile.Sha256Hash;

                    if (sizeChanged || hashChanged)
                    {
                        modifiedFiles.Add(currentFile);
                        totalBytes += currentFile.SizeBytes;
                    }
                }
            }

            // Detect deleted files (in previous backup but not in current scan)
            var deletedFiles = previousFiles.Keys
                .Where(path => !currentPaths.Contains(path))
                .ToList();

            var delta = new BackupDelta(newFiles, modifiedFiles, deletedFiles, totalBytes);

            LogDeltaComputed(newFiles.Count, modifiedFiles.Count, deletedFiles.Count, totalBytes);
            return delta;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task SaveBackupSuccessAsync(
        Guid runId,
        string commitId,
        IReadOnlyList<FileMetadata> backedUpFiles,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        
        // Ensure device state exists before updating it
        await GetOrCreateDeviceStateAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                var now = DateTimeOffset.UtcNow;
                long totalBytes = 0;

                // Upsert backed up files
                foreach (var file in backedUpFiles)
                {
                    await using var command = new SqliteCommand(BackupStateSql.UpsertBackupFileQuery, connection, transaction);
                    command.Parameters.AddWithValue("@RelativePath", file.FilePath);
                    command.Parameters.AddWithValue("@Sha256Hash", file.Hash ?? string.Empty);
                    command.Parameters.AddWithValue("@SizeBytes", file.SizeBytes);
                    command.Parameters.AddWithValue("@LastModifiedUtc", file.LastModified.ToString("O"));
                    command.Parameters.AddWithValue("@BackedUpAt", now.ToString("O"));
                    command.Parameters.AddWithValue("@BackupRunId", runId.ToString());
                    command.Parameters.AddWithValue("@UniqueFileId", file.Hash != null ? $"{file.Hash}_{now:yyyyMMddTHHmmssZ}" : DBNull.Value);

                    await command.ExecuteNonQueryAsync(cancellationToken);
                    totalBytes += file.SizeBytes;
                }

                // Update device state
                await using var updateCommand = new SqliteCommand(BackupStateSql.UpdateDeviceStateQuery, connection, transaction);
                updateCommand.Parameters.AddWithValue("@LastSuccessfulBackup", now.ToString("O"));
                updateCommand.Parameters.AddWithValue("@LastRunId", runId.ToString());
                updateCommand.Parameters.AddWithValue("@LastCommitId", commitId);

                await updateCommand.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                LogBackupStateSaved(runId, backedUpFiles.Count, totalBytes);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<BackupFileState?> GetFileStateAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.SelectFileByPathQuery, connection);
            command.Parameters.AddWithValue("@RelativePath", relativePath);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (await reader.ReadAsync(cancellationToken))
            {
                return new BackupFileState(
                    RelativePath: reader.GetString(0),
                    Sha256Hash: reader.GetString(1),
                    SizeBytes: reader.GetInt64(2),
                    LastModifiedUtc: DateTimeOffset.Parse(reader.GetString(3)),
                    BackedUpAt: DateTimeOffset.Parse(reader.GetString(4)),
                    BackupRunId: Guid.Parse(reader.GetString(5)),
                    UniqueFileId: reader.IsDBNull(6) ? null : reader.GetString(6));
            }

            return null;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<BackupFileState>> GetAllFileStatesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var files = new List<BackupFileState>();

            await using var command = new SqliteCommand(BackupStateSql.SelectAllBackupFilesQuery, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                files.Add(new BackupFileState(
                    RelativePath: reader.GetString(0),
                    Sha256Hash: reader.GetString(1),
                    SizeBytes: reader.GetInt64(2),
                    LastModifiedUtc: DateTimeOffset.Parse(reader.GetString(3)),
                    BackedUpAt: DateTimeOffset.Parse(reader.GetString(4)),
                    BackupRunId: Guid.Parse(reader.GetString(5)),
                    UniqueFileId: reader.IsDBNull(6) ? null : reader.GetString(6)));
            }

            return files;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task RemoveDeletedFilesAsync(IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        if (relativePaths.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                foreach (var path in relativePaths)
                {
                    await using var command = new SqliteCommand(BackupStateSql.DeleteFileByPathQuery, connection, transaction);
                    command.Parameters.AddWithValue("@RelativePath", path);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                LogDeletedFilesRemoved(relativePaths.Count);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task UpsertFileStateBatchAsync(
        IReadOnlyList<FileMetadata> files,
        string baseDirectory,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                var backedUpAt = DateTimeOffset.UtcNow;

                foreach (var file in files)
                {
                    // Compute relative path from base directory for consistent storage
                    var relativePath = Path.GetRelativePath(baseDirectory, file.FilePath);

                    await using var command = new SqliteCommand(BackupStateSql.UpsertBackupFileQuery, connection, transaction);
                    command.Parameters.AddWithValue("@RelativePath", relativePath);
                    command.Parameters.AddWithValue("@Sha256Hash", file.Hash ?? string.Empty);
                    command.Parameters.AddWithValue("@SizeBytes", file.SizeBytes);
                    command.Parameters.AddWithValue("@LastModifiedUtc", file.LastModified.ToString("O"));
                    command.Parameters.AddWithValue("@BackedUpAt", backedUpAt.ToString("O"));
                    command.Parameters.AddWithValue("@BackupRunId", runId.ToString());
                    command.Parameters.AddWithValue("@UniqueFileId", DBNull.Value);

                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                LogFileStatesBatchUpserted(files.Count, runId);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Internal method to upsert file states with pre-computed storage paths.
    /// Used by multi-target backup where paths include target prefix.
    /// </summary>
    internal async Task UpsertFileStateBatchWithExplicitPathsAsync(
        IReadOnlyList<(string StoragePath, FileMetadata File)> filesWithPaths,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (filesWithPaths.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                var backedUpAt = DateTimeOffset.UtcNow;

                foreach (var (storagePath, file) in filesWithPaths)
                {
                    await using var command = new SqliteCommand(BackupStateSql.UpsertBackupFileQuery, connection, transaction);
                    command.Parameters.AddWithValue("@RelativePath", storagePath); // Use storage path directly
                    command.Parameters.AddWithValue("@Sha256Hash", file.Hash ?? string.Empty);
                    command.Parameters.AddWithValue("@SizeBytes", file.SizeBytes);
                    command.Parameters.AddWithValue("@LastModifiedUtc", file.LastModified.ToString("O"));
                    command.Parameters.AddWithValue("@BackedUpAt", backedUpAt.ToString("O"));
                    command.Parameters.AddWithValue("@BackupRunId", runId.ToString());
                    command.Parameters.AddWithValue("@UniqueFileId", DBNull.Value);

                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                LogFileStatesBatchUpserted(filesWithPaths.Count, runId);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.CreateSchemaScript, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
            LogDatabaseInitialized();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public void Dispose()
    {
        _dbLock.Dispose();
    }

    #region Logging

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Database initialized successfully")]
    private partial void LogDatabaseInitialized();

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Device state loaded: DeviceId={DeviceId}, TotalFiles={TotalFiles}")]
    private partial void LogDeviceStateLoaded(Guid deviceId, long totalFiles);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "New device state created: DeviceId={DeviceId}")]
    private partial void LogDeviceStateCreated(Guid deviceId);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Delta computed: New={NewCount}, Modified={ModifiedCount}, Deleted={DeletedCount}, TotalBytes={TotalBytes}")]
    private partial void LogDeltaComputed(int newCount, int modifiedCount, int deletedCount, long totalBytes);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Backup state saved: RunId={RunId}, Files={FileCount}, Bytes={TotalBytes}")]
    private partial void LogBackupStateSaved(Guid runId, int fileCount, long totalBytes);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Removed {Count} deleted file records from state")]
    private partial void LogDeletedFilesRemoved(int count);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Batch upserted {Count} file states for run {RunId}")]
    private partial void LogFileStatesBatchUpserted(int count, Guid runId);

    #endregion
}