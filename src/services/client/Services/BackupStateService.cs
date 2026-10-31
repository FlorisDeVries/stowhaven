using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Data;
using FlorisDeV.BackupClient.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
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

    public async Task<LocalCompletionResult> CompletePendingRunAsync(
        Guid deviceId, Guid runId, Guid commitId, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty || runId == Guid.Empty || commitId == Guid.Empty)
            throw new InvalidOperationException("Local completion requires nonempty device, run and commit identifiers.");
        await EnsureInitializedAsync(cancellationToken);
        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            long totalFiles;
            long totalBytes;
            try
            {
                await using (var current = new SqliteCommand(BackupStateSql.SelectDeviceStateQuery, connection, transaction))
                await using (var reader = await current.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken) ||
                        !Guid.TryParse(reader.GetString(0), out var currentDevice) || currentDevice != deviceId)
                        throw new InvalidOperationException("Completion device did not match local device state.");

                    var pending = await ReadPendingRunAsync(connection, transaction, deviceId, cancellationToken);
                    if (pending?.RunId != runId)
                    {
                        if (!reader.IsDBNull(2) && Guid.TryParse(reader.GetString(2), out var lastRun) && lastRun == runId &&
                            !reader.IsDBNull(3) && Guid.TryParse(reader.GetString(3), out var lastCommit) && lastCommit == commitId)
                            return LocalCompletionResult.AlreadyCompleted;
                        throw new InvalidOperationException("No matching pending run or latest completed identity was found.");
                    }
                    if (pending.CommitId != commitId || !pending.ManifestUploaded)
                        throw new InvalidOperationException("Completion identity did not match the submitted pending run.");
                }

                var now = DateTimeOffset.UtcNow;
                await using var command = new SqliteCommand(
                    BackupStateSql.UpsertBackupFilesFromPendingRunQuery + ";" +
                    BackupStateSql.DeleteBackupFilesForRunDeletionsQuery + ";" +
                    BackupStateSql.UpdateDeviceStateQuery + ";" +
                    BackupStateSql.DeletePendingRunFilesQuery + ";" +
                    BackupStateSql.DeletePendingRunDeletionsQuery + ";" +
                    BackupStateSql.DeletePendingBackupRunQuery, connection, transaction);
                command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
                command.Parameters.AddWithValue("@RunId", runId.ToString());
                command.Parameters.AddWithValue("@BackedUpAt", now.ToString("O"));
                command.Parameters.AddWithValue("@LastSuccessfulBackup", now.ToString("O"));
                command.Parameters.AddWithValue("@LastRunId", runId.ToString());
                command.Parameters.AddWithValue("@LastCommitId", commitId.ToString("N"));
                await command.ExecuteNonQueryAsync(cancellationToken);

                await using (var totals = new SqliteCommand(BackupStateSql.SelectDeviceStateTotalsQuery, connection, transaction))
                await using (var reader = await totals.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("Device state was missing during local completion.");
                    totalFiles = reader.GetInt64(0);
                    totalBytes = reader.GetInt64(1);
                }

                // Cancellation can still roll everything back until commit starts. Once it starts,
                // settle the database outcome even if the caller cancels during commit.
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch
            {
                if (transaction.Connection is not null)
                    await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            LogBackupStateSaved(runId, totalFiles, totalBytes);
            return LocalCompletionResult.Completed;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public string DatabasePath { get; }

    public BackupStateService(
        IOptions<DatabaseOptions> options,
        ILogger<BackupStateService> logger)
    {
        _logger = logger;

        var databasePath = Path.GetFullPath(options.Value.GetDatabasePath());
        DatabasePath = databasePath;

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

    public async Task<PendingBackupRun?> GetPendingBackupRunAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var header = await ReadPendingRunAsync(connection, null, deviceId, cancellationToken);
            if (header == null)
            {
                return null;
            }

            // The journal contents stay on disk; only their sizes are surfaced on the header.
            return header with
            {
                UploadedFileCount = await CountAsync(
                    connection, BackupStateSql.CountPendingRunFilesQuery, header.DeviceId, header.RunId, cancellationToken),
                DeletedFileCount = await CountAsync(
                    connection, BackupStateSql.CountPendingRunDeletionsQuery, header.DeviceId, header.RunId, cancellationToken)
            };
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private static async Task<PendingBackupRun?> ReadPendingRunAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(BackupStateSql.SelectPendingBackupRunQuery, connection, transaction);
        command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var header = JsonSerializer.Deserialize<PendingBackupRun>(reader.GetString(2), PendingRunJsonOptions);
        if (header is null || header.DeviceId != deviceId || header.RunId == Guid.Empty || header.CommitId == Guid.Empty ||
            !Guid.TryParse(reader.GetString(0), out var storedDevice) || storedDevice != header.DeviceId ||
            !Guid.TryParse(reader.GetString(1), out var storedRun) || storedRun != header.RunId)
            throw new InvalidOperationException("Stored pending run identifiers did not match its payload.");
        return header;
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string query,
        Guid deviceId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqliteCommand(query, connection);
        command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
        command.Parameters.AddWithValue("@RunId", runId.ToString());

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
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

    public Task ClearPendingBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
        => ClearRunAsync(deviceId, runId, invalidateTrackedFiles: false, cancellationToken);

    public Task RecoverFailedRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
        => ClearRunAsync(deviceId, runId, invalidateTrackedFiles: true, cancellationToken);

    private async Task ClearRunAsync(Guid deviceId, Guid runId, bool invalidateTrackedFiles, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                if (invalidateTrackedFiles)
                {
                    await using var invalidate = new SqliteCommand(BackupStateSql.InvalidateFailedRunFilesQuery, connection, transaction);
                    invalidate.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
                    invalidate.Parameters.AddWithValue("@RunId", runId.ToString());
                    await invalidate.ExecuteNonQueryAsync(cancellationToken);
                    await using var totals = new SqliteCommand(BackupStateSql.UpdateDeviceStateTotalsQuery, connection, transaction);
                    await totals.ExecuteNonQueryAsync(cancellationToken);
                }
                // The header and its journal rows are cleared together; a header without its
                // journal would look like a resumable run with nothing uploaded.
                foreach (var query in (string[])
                         [
                             BackupStateSql.DeletePendingRunFilesQuery,
                             BackupStateSql.DeletePendingRunDeletionsQuery,
                             BackupStateSql.DeletePendingBackupRunQuery
                         ])
                {
                    await using var command = new SqliteCommand(query, connection, transaction);
                    command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
                    command.Parameters.AddWithValue("@RunId", runId.ToString());
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
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

    public async Task AppendPendingRunFilesAsync(
        Guid deviceId,
        Guid runId,
        IReadOnlyList<TaggedFile> files,
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
                foreach (var file in files)
                {
                    await using var command = new SqliteCommand(BackupStateSql.UpsertPendingRunFileQuery, connection, transaction);
                    command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
                    command.Parameters.AddWithValue("@RunId", runId.ToString());
                    command.Parameters.AddWithValue("@StoragePath", file.GetStoragePath());
                    command.Parameters.AddWithValue("@Sha256Hash", file.Metadata.Hash ?? string.Empty);
                    command.Parameters.AddWithValue("@SizeBytes", file.Metadata.SizeBytes);
                    command.Parameters.AddWithValue("@LastModifiedUtc", file.Metadata.LastModified.ToString("O"));
                    command.Parameters.AddWithValue("@UniqueFileId", (object?)file.UniqueFileId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@PayloadJson", JsonSerializer.Serialize(file, PendingRunJsonOptions));

                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
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

    public async Task<TaggedFile?> FindStagedRunFileAsync(
        Guid deviceId,
        Guid runId,
        string storagePath,
        string? sha256Hash,
        long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.SelectPendingRunFileQuery, connection);
            command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
            command.Parameters.AddWithValue("@RunId", runId.ToString());
            command.Parameters.AddWithValue("@StoragePath", storagePath);
            command.Parameters.AddWithValue("@Sha256Hash", sha256Hash ?? string.Empty);
            command.Parameters.AddWithValue("@SizeBytes", sizeBytes);

            var payload = (string?)await command.ExecuteScalarAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize<TaggedFile>(payload, PendingRunJsonOptions);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async IAsyncEnumerable<TaggedFile> StreamPendingRunFilesAsync(
        Guid deviceId,
        Guid runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.SelectPendingRunFilesQuery, connection);
            command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
            command.Parameters.AddWithValue("@RunId", runId.ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var file = JsonSerializer.Deserialize<TaggedFile>(reader.GetString(0), PendingRunJsonOptions);
                if (file != null)
                {
                    yield return file;
                }
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task RemovePendingRunFilesAsync(
        Guid deviceId,
        Guid runId,
        IReadOnlyList<string> storagePaths,
        CancellationToken cancellationToken = default)
    {
        if (storagePaths.Count == 0)
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
                foreach (var storagePath in storagePaths)
                {
                    await using var command = new SqliteCommand(
                        BackupStateSql.DeletePendingRunFileByPathQuery,
                        connection,
                        transaction);
                    command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
                    command.Parameters.AddWithValue("@RunId", runId.ToString());
                    command.Parameters.AddWithValue("@StoragePath", storagePath);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
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

    public async Task RemoveTrackedFilesAsync(
        IReadOnlyList<string> storagePaths,
        CancellationToken cancellationToken = default)
    {
        if (storagePaths.Count == 0)
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
                var removed = 0;
                foreach (var storagePath in storagePaths)
                {
                    await using var command = new SqliteCommand(
                        BackupStateSql.DeleteFileByPathQuery,
                        connection,
                        transaction);
                    command.Parameters.AddWithValue("@RelativePath", storagePath);
                    removed += await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var updateCommand = new SqliteCommand(
                    BackupStateSql.UpdateDeviceStateTotalsQuery,
                    connection,
                    transaction);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                if (removed > 0)
                {
                    LogRejectedTrackedFilesRemoved(removed);
                }
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

    public async Task BeginScanAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.TruncateScanScratchPathsQuery, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task AppendScannedPathsAsync(IReadOnlyList<string> storagePaths, CancellationToken cancellationToken = default)
    {
        if (storagePaths.Count == 0)
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
                foreach (var path in storagePaths)
                {
                    await using var command = new SqliteCommand(BackupStateSql.InsertScanScratchPathQuery, connection, transaction);
                    command.Parameters.AddWithValue("@StoragePath", path);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
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

    public async Task<int> CountScanDeletionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.CountScanDeletionsQuery, connection);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<int> RecordScanDeletionsAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            int count;
            try
            {
                // Re-derived from scratch: a file recorded as deleted by an earlier attempt at this
                // run may have reappeared before the retry scanned it.
                await using var deleteCommand = new SqliteCommand(BackupStateSql.DeletePendingRunDeletionsQuery, connection, transaction);
                deleteCommand.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
                deleteCommand.Parameters.AddWithValue("@RunId", runId.ToString());
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

                await using var insertCommand = new SqliteCommand(BackupStateSql.InsertPendingRunDeletionsQuery, connection, transaction);
                insertCommand.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
                insertCommand.Parameters.AddWithValue("@RunId", runId.ToString());
                count = await insertCommand.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }

            if (count > 0)
            {
                LogDeletedFilesRecorded(count, runId);
            }

            return count;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async IAsyncEnumerable<string> StreamPendingRunDeletionsAsync(
        Guid deviceId,
        Guid runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqliteCommand(BackupStateSql.SelectPendingRunDeletionsQuery, connection);
            command.Parameters.AddWithValue("@DeviceId", deviceId.ToString());
            command.Parameters.AddWithValue("@RunId", runId.ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                yield return reader.GetString(0);
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

            await using var versionCommand = new SqliteCommand("PRAGMA user_version", connection);
            var existingVersion = Convert.ToInt64(await versionCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

            await using var command = new SqliteCommand(BackupStateSql.CreateSchemaScript, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            if (existingVersion < BackupStateSql.SchemaVersion)
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                var dropped = 0;
                if (existingVersion < 2)
                {
                    await using var upgradeCommand = new SqliteCommand(BackupStateSql.DropLegacyPendingRunsScript, connection, transaction);
                    dropped = await upgradeCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var migrateCommand = new SqliteCommand(BackupStateSql.MigratePathCollationScript, connection, transaction);
                await migrateCommand.ExecuteNonQueryAsync(cancellationToken);
                await using var stampCommand = new SqliteCommand(
                    $"PRAGMA user_version = {BackupStateSql.SchemaVersion}", connection, transaction);
                await stampCommand.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                LogSchemaUpgraded(existingVersion, BackupStateSql.SchemaVersion, dropped);
            }

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
