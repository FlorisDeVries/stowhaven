using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupApi.Services;

public interface IManifestManager
{
    Task<BackupRun> CreateBackupRunAsync(Guid deviceId, Guid runId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    Task<BackupRun> UpdateBackupRunAsync(Guid deviceId, Guid runId, BackupRun updatedRun,
        CancellationToken cancellationToken = default);

    Task<BackupRunPage> GetBackupRunsPageAsync(BackupRunQuery query, CancellationToken cancellationToken = default);

    Task SaveRunManifestAsync(Guid deviceId, Guid runId, RunManifest manifest, CancellationToken cancellationToken = default);

    Task<RunManifest?> GetRunManifestAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    Task<CommitJob> CreateCommitJobAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    Task<(bool Claimed, CommitJob CommitJob)> TryClaimCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default);

    Task<CommitJob> GetCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default);

    Task<CommitJobPage> GetCommitJobsPageAsync(CommitJobQuery query, CancellationToken cancellationToken = default);

    Task<CommitJob> UpdateCommitJobAsync(CommitJob commitJob, CancellationToken cancellationToken = default);

    Task<CommitFileProgress?> GetCommitFileProgressAsync(Guid commitId, string uniqueFileId, CancellationToken cancellationToken = default);

    Task<CommitFileProgressPage> GetCommitFileProgressPageAsync(Guid commitId, int pageSize, string? continuationToken = null,
        CancellationToken cancellationToken = default);

    Task<CommitFileProgress> SaveCommitFileProgressAsync(CommitFileProgress progress, CancellationToken cancellationToken = default);

    Task<FileEntry?> GetFileEntryAsync(Guid deviceId, string relativePath, CancellationToken cancellationToken = default);

    Task SaveFileEntryAsync(FileEntry fileEntry, CancellationToken cancellationToken = default);

    Task<FileVersion?> GetFileVersionAsync(Guid deviceId, string uniqueFileId, CancellationToken cancellationToken = default);

    Task SaveFileVersionAsync(FileVersion fileVersion, CancellationToken cancellationToken = default);

    Task<List<FileEntry>> GetAllFileEntriesAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task<FileEntryPage> GetFileEntriesPageAsync(Guid deviceId, int pageSize, string? continuationToken = null,
        CancellationToken cancellationToken = default);
}
