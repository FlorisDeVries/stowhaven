using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Data;
using FlorisDeV.BackupClient.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// SQLite-based implementation of backup state management.
/// Handles database operations for tracking backup state and file history.
/// </summary>
public partial class BackupStateService : IBackupStateService, IDisposable
{
    private static readonly JsonSerializerOptions PendingRunJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _connectionString;
    private readonly ILogger<BackupStateService> _logger;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _initialized;

    public BackupStateService(
        IOptions<DatabaseOptions> options,
        ILogger<BackupStateService> logger)
    {
        _logger = logger;

        var databasePath = options.Value.GetDatabasePath();

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

    /// <summary>
    /// Upserts tagged file states in a single transaction.
    /// Storage paths are computed from the tagged files (includes target name prefix).
    /// </summary>
    public async Task UpsertFileStateBatchAsync(
        IReadOnlyList<TaggedFile> taggedFiles,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (taggedFiles.Count == 0)
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

                foreach (var taggedFile in taggedFiles)
                {
                    var storagePath = taggedFile.GetStoragePath();
                    var file = taggedFile.Metadata;

                    await using var command = new SqliteCommand(BackupStateSql.UpsertBackupFileQuery, connection, transaction);
                    command.Parameters.AddWithValue("@RelativePath", storagePath);
                    command.Parameters.AddWithValue("@Sha256Hash", file.Hash ?? string.Empty);
                    command.Parameters.AddWithValue("@SizeBytes", file.SizeBytes);
                    command.Parameters.AddWithValue("@LastModifiedUtc", file.LastModified.ToString("O"));
                    command.Parameters.AddWithValue("@BackedUpAt", backedUpAt.ToString("O"));
                    command.Parameters.AddWithValue("@BackupRunId", runId.ToString());
                    command.Parameters.AddWithValue("@UniqueFileId", DBNull.Value);

                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                LogFileStatesBatchUpserted(taggedFiles.Count, runId);
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

    public async Task<PendingBackupRun?> GetPendingBackupRunAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.SelectPendingBackupRunQuery, connection);
            command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());

            var payload = (string?)await command.ExecuteScalarAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize<PendingBackupRun>(payload, PendingRunJsonOptions);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task SavePendingBackupRunAsync(PendingBackupRun pendingRun, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.UpsertPendingBackupRunQuery, connection);
            command.Parameters.AddWithValue("@DeviceId", pendingRun.DeviceId.ToString());
            command.Parameters.AddWithValue("@RunId", pendingRun.RunId.ToString());
            command.Parameters.AddWithValue("@PayloadJson", JsonSerializer.Serialize(pendingRun, PendingRunJsonOptions));
            command.Parameters.AddWithValue("@UpdatedAt", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task ClearPendingBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.DeletePendingBackupRunQuery, connection);
            command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
            command.Parameters.AddWithValue("@RunId", runId.ToString());

            await command.ExecuteNonQueryAsync(cancellationToken);
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
}
