namespace FlorisDeV.BackupClient.Data;

/// <summary>
/// SQL scripts for BackupStateService.
/// Contains schema definitions and parameterized queries.
/// </summary>
internal static class BackupStateSql
{
    /// <summary>
    /// Creates the database schema if it doesn't exist.
    /// Includes tables and indexes for optimal query performance.
    /// </summary>
    internal const string CreateSchemaScript = @"
        -- Device and backup metadata (single row)
        CREATE TABLE IF NOT EXISTS DeviceState (
            DeviceId TEXT PRIMARY KEY,
            LastSuccessfulBackup TEXT,
            LastRunId TEXT,
            LastCommitId TEXT,
            TotalFilesTracked INTEGER NOT NULL DEFAULT 0,
            TotalBytesTracked INTEGER NOT NULL DEFAULT 0
        );

        -- File state from last successful backup
        CREATE TABLE IF NOT EXISTS BackupFiles (
            RelativePath TEXT PRIMARY KEY,
            Sha256Hash TEXT NOT NULL,
            SizeBytes INTEGER NOT NULL,
            LastModifiedUtc TEXT NOT NULL,
            BackedUpAt TEXT NOT NULL,
            BackupRunId TEXT NOT NULL,
            UniqueFileId TEXT
        );

        -- Indexes for performance
        CREATE INDEX IF NOT EXISTS idx_backupfiles_hash ON BackupFiles(Sha256Hash);
        CREATE INDEX IF NOT EXISTS idx_backupfiles_runid ON BackupFiles(BackupRunId);
        CREATE INDEX IF NOT EXISTS idx_backupfiles_backedup ON BackupFiles(BackedUpAt);
    ";

    /// <summary>
    /// Selects the device state record.
    /// </summary>
    internal const string SelectDeviceStateQuery = 
        "SELECT DeviceId, LastSuccessfulBackup, LastRunId, LastCommitId, TotalFilesTracked, TotalBytesTracked FROM DeviceState LIMIT 1";

    /// <summary>
    /// Inserts a new device state record.
    /// </summary>
    internal const string InsertDeviceStateQuery = @"
        INSERT INTO DeviceState (DeviceId, LastSuccessfulBackup, LastRunId, LastCommitId, TotalFilesTracked, TotalBytesTracked)
        VALUES (@DeviceId, @LastSuccessfulBackup, @LastRunId, @LastCommitId, @TotalFilesTracked, @TotalBytesTracked)";

    /// <summary>
    /// Updates device state after successful backup.
    /// Also recalculates aggregates from BackupFiles table.
    /// </summary>
    internal const string UpdateDeviceStateQuery = @"
        UPDATE DeviceState
        SET LastSuccessfulBackup = @LastSuccessfulBackup,
            LastRunId = @LastRunId,
            LastCommitId = @LastCommitId,
            TotalFilesTracked = COALESCE((SELECT COUNT(*) FROM BackupFiles), 0),
            TotalBytesTracked = COALESCE((SELECT SUM(SizeBytes) FROM BackupFiles), 0)";

    /// <summary>
    /// Selects all backed-up files for delta computation.
    /// </summary>
    internal const string SelectAllBackupFilesQuery = 
        "SELECT RelativePath, Sha256Hash, SizeBytes, LastModifiedUtc, BackedUpAt, BackupRunId, UniqueFileId FROM BackupFiles";

    /// <summary>
    /// Selects a single file state by relative path (case-insensitive).
    /// </summary>
    internal const string SelectFileByPathQuery = 
        "SELECT RelativePath, Sha256Hash, SizeBytes, LastModifiedUtc, BackedUpAt, BackupRunId, UniqueFileId FROM BackupFiles WHERE RelativePath = @RelativePath COLLATE NOCASE";

    /// <summary>
    /// Upserts (insert or update) a backup file record.
    /// Uses SQLite's ON CONFLICT to handle updates efficiently.
    /// </summary>
    internal const string UpsertBackupFileQuery = @"
        INSERT INTO BackupFiles (RelativePath, Sha256Hash, SizeBytes, LastModifiedUtc, BackedUpAt, BackupRunId, UniqueFileId)
        VALUES (@RelativePath, @Sha256Hash, @SizeBytes, @LastModifiedUtc, @BackedUpAt, @BackupRunId, @UniqueFileId)
        ON CONFLICT(RelativePath) DO UPDATE SET
            Sha256Hash = excluded.Sha256Hash,
            SizeBytes = excluded.SizeBytes,
            LastModifiedUtc = excluded.LastModifiedUtc,
            BackedUpAt = excluded.BackedUpAt,
            BackupRunId = excluded.BackupRunId,
            UniqueFileId = excluded.UniqueFileId";

    /// <summary>
    /// Deletes a file record by relative path (case-insensitive).
    /// Used when cleaning up deleted files from state.
    /// </summary>
    internal const string DeleteFileByPathQuery = 
        "DELETE FROM BackupFiles WHERE RelativePath = @RelativePath COLLATE NOCASE";
}
