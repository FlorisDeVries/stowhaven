namespace FlorisDeV.BackupClient.Data;

/// <summary>
/// SQL scripts for BackupStateService.
/// Contains schema definitions and parameterized queries.
/// </summary>
internal static class BackupStateSql
{
    /// <summary>
    /// Current schema version, tracked in <c>PRAGMA user_version</c>. Version 2 replaced the
    /// single-row in-flight journal (every uploaded file inlined into one JSON payload) with the
    /// append-only <c>PendingRunFiles</c> / <c>PendingRunDeletions</c> tables.
    /// </summary>
    internal const long SchemaVersion = 2;

    /// <summary>
    /// Creates the database schema if it doesn't exist.
    /// Includes tables and indexes for optimal query performance.
    /// Also enables SQLite optimizations like WAL mode for better concurrency.
    /// </summary>
    internal const string CreateSchemaScript = @"
        -- Enable WAL mode for better concurrency (allows concurrent reads during writes)
        PRAGMA journal_mode=WAL;

        -- Optimize for speed
        PRAGMA synchronous=NORMAL;
        PRAGMA cache_size=-64000;  -- 64MB cache
        PRAGMA temp_store=MEMORY;

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

        -- Durable in-flight backup run journal. One active run is kept per device. This row holds
        -- only run-level metadata (a few hundred bytes); the per-file entries live in
        -- PendingRunFiles so the journal is appended to rather than rewritten on every batch.
        CREATE TABLE IF NOT EXISTS PendingBackupRuns (
            DeviceId TEXT PRIMARY KEY,
            RunId TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        -- Append-only record of blobs already staged for an in-flight run. A resumed run matches
        -- against this to skip re-uploading, and the run manifest is streamed straight out of it,
        -- so the uploaded set never has to be held in memory.
        CREATE TABLE IF NOT EXISTS PendingRunFiles (
            DeviceId TEXT NOT NULL,
            RunId TEXT NOT NULL,
            StoragePath TEXT NOT NULL COLLATE NOCASE,
            Sha256Hash TEXT NOT NULL,
            SizeBytes INTEGER NOT NULL,
            LastModifiedUtc TEXT NOT NULL,
            UniqueFileId TEXT,
            PayloadJson TEXT NOT NULL,
            PRIMARY KEY (DeviceId, RunId, StoragePath)
        );

        -- Deletions recorded for an in-flight run, so a resumed run reproduces the same manifest.
        CREATE TABLE IF NOT EXISTS PendingRunDeletions (
            DeviceId TEXT NOT NULL,
            RunId TEXT NOT NULL,
            StoragePath TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (DeviceId, RunId, StoragePath)
        );

        -- Every path seen by the current scan. Deletion detection is a SQL anti-join against
        -- BackupFiles, so neither the scanned set nor the deletion set is ever materialized in
        -- memory. Truncated at the start of each scan.
        CREATE TABLE IF NOT EXISTS ScanScratchPaths (
            StoragePath TEXT PRIMARY KEY COLLATE NOCASE
        );

        -- Indexes for performance
        CREATE INDEX IF NOT EXISTS idx_backupfiles_hash ON BackupFiles(Sha256Hash);
        CREATE INDEX IF NOT EXISTS idx_backupfiles_runid ON BackupFiles(BackupRunId);
        CREATE INDEX IF NOT EXISTS idx_backupfiles_backedup ON BackupFiles(BackedUpAt);
    ";

    /// <summary>
    /// Drops in-flight journal rows written by schema v1. Those rows inlined every uploaded file
    /// into a single payload and have no matching PendingRunFiles rows, so they cannot be resumed
    /// against the v2 schema. Dropping them costs a re-upload of the interrupted run's staged
    /// blobs; the orphaned staging blobs age out via the storage account's lifecycle rule.
    /// </summary>
    internal const string DropLegacyPendingRunsScript = "DELETE FROM PendingBackupRuns";

    /// <summary>
    /// Selects the device state record.
    /// </summary>
    internal const string SelectDeviceStateQuery =
        "SELECT DeviceId, LastSuccessfulBackup, LastRunId, LastCommitId, TotalFilesTracked, TotalBytesTracked FROM DeviceState LIMIT 1";

    internal const string SelectDeviceStateTotalsQuery =
        "SELECT TotalFilesTracked, TotalBytesTracked FROM DeviceState LIMIT 1";

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

    internal const string UpdateDeviceStateTotalsQuery = @"
        UPDATE DeviceState
        SET TotalFilesTracked = COALESCE((SELECT COUNT(*) FROM BackupFiles), 0),
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
    /// Used when cleaning up deleted file tracking.
    /// </summary>
    internal const string DeleteFileByPathQuery =
        "DELETE FROM BackupFiles WHERE RelativePath = @RelativePath COLLATE NOCASE";

    internal const string SelectPendingBackupRunQuery =
        "SELECT PayloadJson FROM PendingBackupRuns WHERE DeviceId = @DeviceId";

    internal const string UpsertPendingBackupRunQuery = @"
        INSERT INTO PendingBackupRuns (DeviceId, RunId, PayloadJson, UpdatedAt)
        VALUES (@DeviceId, @RunId, @PayloadJson, @UpdatedAt)
        ON CONFLICT(DeviceId) DO UPDATE SET
            RunId = excluded.RunId,
            PayloadJson = excluded.PayloadJson,
            UpdatedAt = excluded.UpdatedAt";

    internal const string DeletePendingBackupRunQuery =
        "DELETE FROM PendingBackupRuns WHERE DeviceId = @DeviceId AND RunId = @RunId";

    /// <summary>
    /// Appends one staged file to the in-flight journal. Re-appending the same path is a no-op
    /// update, which keeps batch retries idempotent.
    /// </summary>
    internal const string UpsertPendingRunFileQuery = @"
        INSERT INTO PendingRunFiles (DeviceId, RunId, StoragePath, Sha256Hash, SizeBytes, LastModifiedUtc, UniqueFileId, PayloadJson)
        VALUES (@DeviceId, @RunId, @StoragePath, @Sha256Hash, @SizeBytes, @LastModifiedUtc, @UniqueFileId, @PayloadJson)
        ON CONFLICT(DeviceId, RunId, StoragePath) DO UPDATE SET
            Sha256Hash = excluded.Sha256Hash,
            SizeBytes = excluded.SizeBytes,
            LastModifiedUtc = excluded.LastModifiedUtc,
            UniqueFileId = excluded.UniqueFileId,
            PayloadJson = excluded.PayloadJson";

    /// <summary>
    /// Looks up a single already-staged file for resume. Size and hash must match the freshly
    /// scanned file, otherwise the local copy changed and the staged blob is stale.
    /// </summary>
    internal const string SelectPendingRunFileQuery = @"
        SELECT PayloadJson FROM PendingRunFiles
        WHERE DeviceId = @DeviceId AND RunId = @RunId AND StoragePath = @StoragePath
          AND Sha256Hash = @Sha256Hash AND SizeBytes = @SizeBytes";

    internal const string CountPendingRunFilesQuery =
        "SELECT COUNT(*) FROM PendingRunFiles WHERE DeviceId = @DeviceId AND RunId = @RunId";

    /// <summary>
    /// Streams the journal in primary-key order so the manifest can be written without sorting
    /// or buffering.
    /// </summary>
    internal const string SelectPendingRunFilesQuery =
        "SELECT PayloadJson FROM PendingRunFiles WHERE DeviceId = @DeviceId AND RunId = @RunId ORDER BY StoragePath";

    internal const string DeletePendingRunFilesQuery =
        "DELETE FROM PendingRunFiles WHERE DeviceId = @DeviceId AND RunId = @RunId";

    internal const string DeletePendingRunFileByPathQuery = @"
        DELETE FROM PendingRunFiles
        WHERE DeviceId = @DeviceId AND RunId = @RunId AND StoragePath = @StoragePath";

    /// <summary>
    /// Records this run's deletions as the set of tracked files the current scan never saw.
    /// Runs entirely inside SQLite so neither side of the comparison reaches managed memory.
    /// </summary>
    internal const string InsertPendingRunDeletionsQuery = @"
        INSERT OR IGNORE INTO PendingRunDeletions (DeviceId, RunId, StoragePath)
        SELECT @DeviceId, @RunId, b.RelativePath
        FROM BackupFiles b
        WHERE NOT EXISTS (SELECT 1 FROM ScanScratchPaths s WHERE s.StoragePath = b.RelativePath)";

    internal const string CountPendingRunDeletionsQuery =
        "SELECT COUNT(*) FROM PendingRunDeletions WHERE DeviceId = @DeviceId AND RunId = @RunId";

    /// <summary>
    /// Counts this scan's deletions before a run exists to journal them against, so a
    /// deletion-only run can be started (or the backup skipped) without materializing the set.
    /// </summary>
    internal const string CountScanDeletionsQuery = @"
        SELECT COUNT(*) FROM BackupFiles b
        WHERE NOT EXISTS (SELECT 1 FROM ScanScratchPaths s WHERE s.StoragePath = b.RelativePath)";

    internal const string SelectPendingRunDeletionsQuery =
        "SELECT StoragePath FROM PendingRunDeletions WHERE DeviceId = @DeviceId AND RunId = @RunId ORDER BY StoragePath";

    internal const string DeletePendingRunDeletionsQuery =
        "DELETE FROM PendingRunDeletions WHERE DeviceId = @DeviceId AND RunId = @RunId";

    /// <summary>
    /// Drops tracked file state for everything this run recorded as deleted. Set-based so the
    /// deletion list is never loaded.
    /// </summary>
    internal const string DeleteBackupFilesForRunDeletionsQuery = @"
        DELETE FROM BackupFiles
        WHERE EXISTS (
            SELECT 1 FROM PendingRunDeletions d
            WHERE d.DeviceId = @DeviceId AND d.RunId = @RunId AND d.StoragePath = BackupFiles.RelativePath)";

    /// <summary>
    /// Promotes the in-flight journal into tracked file state. Set-based, so committing a run with
    /// hundreds of thousands of files costs no managed memory.
    /// UniqueFileId is deliberately left null, matching the previous per-batch upsert: local state
    /// drives delta detection only, and blob identity lives in the server-side manifest.
    /// </summary>
    internal const string UpsertBackupFilesFromPendingRunQuery = @"
        INSERT INTO BackupFiles (RelativePath, Sha256Hash, SizeBytes, LastModifiedUtc, BackedUpAt, BackupRunId, UniqueFileId)
        SELECT StoragePath, Sha256Hash, SizeBytes, LastModifiedUtc, @BackedUpAt, @RunId, NULL
        FROM PendingRunFiles
        WHERE DeviceId = @DeviceId AND RunId = @RunId
        ON CONFLICT(RelativePath) DO UPDATE SET
            Sha256Hash = excluded.Sha256Hash,
            SizeBytes = excluded.SizeBytes,
            LastModifiedUtc = excluded.LastModifiedUtc,
            BackedUpAt = excluded.BackedUpAt,
            BackupRunId = excluded.BackupRunId,
            UniqueFileId = excluded.UniqueFileId";

    internal const string TruncateScanScratchPathsQuery = "DELETE FROM ScanScratchPaths";

    internal const string InsertScanScratchPathQuery =
        "INSERT OR IGNORE INTO ScanScratchPaths (StoragePath) VALUES (@StoragePath)";
}
