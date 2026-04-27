using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapr.Client;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Constants;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Logging.OpenTelemetry;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupApi.Services;

public interface IManifestManager
{
    Task<BackupRun> CreateBackupRunAsync(Guid deviceId, Guid runId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    Task<BackupRun> UpdateBackupRunAsync(Guid deviceId, Guid runId, BackupRun updatedRun,
        CancellationToken cancellationToken = default);

    // CommitJob management
    Task<CommitJob> CreateCommitJobAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);

    Task<(bool Claimed, CommitJob CommitJob)> TryClaimCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default);
    
    Task<CommitJob> GetCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default);
    
    Task<CommitJob> UpdateCommitJobAsync(CommitJob commitJob, CancellationToken cancellationToken = default);

    Task<CommitFileProgress?> GetCommitFileProgressAsync(Guid commitId, string uniqueFileId, CancellationToken cancellationToken = default);

    Task<CommitFileProgress> SaveCommitFileProgressAsync(CommitFileProgress progress, CancellationToken cancellationToken = default);

    // File tracking
    Task<FileEntry?> GetFileEntryAsync(Guid deviceId, string relativePath, CancellationToken cancellationToken = default);
    
    Task SaveFileEntryAsync(FileEntry fileEntry, CancellationToken cancellationToken = default);
    
    Task<FileVersion?> GetFileVersionAsync(Guid deviceId, string uniqueFileId, CancellationToken cancellationToken = default);
    
    Task SaveFileVersionAsync(FileVersion fileVersion, CancellationToken cancellationToken = default);
    
    Task<List<FileEntry>> GetAllFileEntriesAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task<FileEntryPage> GetFileEntriesPageAsync(Guid deviceId, int pageSize, string? continuationToken = null,
        CancellationToken cancellationToken = default);
}

public partial class ManifestManager(
    DaprClient daprClient,
    ILogger<ManifestManager> logger,
    TelemetryProvider telemetry
) : IManifestManager
{
    public async Task<BackupRun> CreateBackupRunAsync(Guid deviceId, Guid runId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("CreateBackupRun");
        var stateKey = $"{deviceId}/backupruns/{runId}";

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var state = new BackupRun
        {
            RunId = runId,
            DeviceId = deviceId,
            StartedAt = startedAt,
            Status = BackupRunStatus.Queued,
        };

        await daprClient.SaveStateAsync(DaprComponents.ManifestStateStore, stateKey, state,
            cancellationToken: cancellationToken);

        telemetry.StateOperations.Add(1, new TagList { { "operation", "create" }, { "store", "manifest" } });
        
        LogBackupRunCreated(logger, runId, deviceId);
        return state;
    }

    public async Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetBackupRun");
        var stateKey = $"{deviceId}/backupruns/{runId}";

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var (run, etag) = await daprClient.GetStateAndETagAsync<BackupRun>(
            DaprComponents.ManifestStateStore, 
            stateKey,
            cancellationToken: cancellationToken);
        
        if (run != null)
        {
            // Store the ETag in the model for later use
            run.ETag = etag;
            activity?.SetTag(ActivityAttributes.StateETag, etag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "result", "found" } });
            return run;
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "result", "not_found" } });
        throw new BackupRunNotFoundException(deviceId, runId);
    }

    public async Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var stateKey = $"{deviceId}/backupruns/{runId}";
        
        // Get the current state with ETag
        var (run, etag) = await daprClient.GetStateAndETagAsync<BackupRun>(
            DaprComponents.ManifestStateStore, 
            stateKey,
            cancellationToken: cancellationToken);

        if (run == null)
        {
            throw new BackupRunNotFoundException(deviceId, runId);
        }

        // Validate state transition
        if (run.Status == BackupRunStatus.Succeeded)
        {
            throw new BackupRunAlreadyCommittedException(deviceId, runId);
        }

        if (run.Status == BackupRunStatus.Failed)
        {
            throw new InvalidBackupRunStateException(
                deviceId, 
                runId, 
                run.Status, 
                BackupRunStatus.Queued);
        }

        // Update the run status
        run.Status = BackupRunStatus.Succeeded;
        run.CompletedAt = DateTimeOffset.UtcNow;

        // Attempt to save with ETag for optimistic concurrency control
        var success = await daprClient.TrySaveStateAsync(
            DaprComponents.ManifestStateStore, 
            stateKey, 
            run,
            etag,
            cancellationToken: cancellationToken);

        if (!success)
        {
            // ETag mismatch - concurrent update detected
            LogConcurrentUpdateDetected(logger, runId, deviceId, etag);
            
            throw new ConcurrentUpdateException(deviceId, runId, etag, actualETag: null);
        }

        LogBackupRunCommitted(logger, runId, deviceId, run.Status);

        return run;
    }

    public async Task<BackupRun> UpdateBackupRunAsync(Guid deviceId, Guid runId, BackupRun updatedRun,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("UpdateBackupRun");
        var stateKey = $"{deviceId}/backupruns/{runId}";

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        if (!string.IsNullOrEmpty(updatedRun.ETag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                updatedRun,
                updatedRun.ETag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                LogConcurrentUpdateDetected(logger, runId, deviceId, updatedRun.ETag);
                throw new ConcurrentUpdateException(deviceId, runId, updatedRun.ETag, actualETag: null);
            }
        }
        else
        {
            // No ETag, perform unconditional update
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                updatedRun,
                cancellationToken: cancellationToken);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "update" }, { "store", "manifest" } });
        LogBackupRunUpdated(logger, runId, deviceId, updatedRun.Status);

        return updatedRun;
    }

    public async Task<CommitJob> CreateCommitJobAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("CreateCommitJob");
        var commitId = CreateDeterministicCommitId(deviceId, runId);
        var stateKey = GetCommitJobStateKey(commitId);
        var now = DateTimeOffset.UtcNow;

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());
        activity?.SetTag("commit_id", commitId.ToString());

        var (existingCommitJob, existingEtag) = await daprClient.GetStateAndETagAsync<CommitJob>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (existingCommitJob != null)
        {
            existingCommitJob.ETag = existingEtag;
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "duplicate" } });
            LogCommitJobAlreadyExists(logger, commitId, deviceId, runId, existingCommitJob.Status);
            return existingCommitJob;
        }

        var commitJob = new CommitJob
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            Status = CommitJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };

        await daprClient.SaveStateAsync(DaprComponents.ManifestStateStore, stateKey, commitJob,
            cancellationToken: cancellationToken);

        telemetry.StateOperations.Add(1, new TagList { { "operation", "create" }, { "store", "manifest" }, { "entity", "commitjob" } });
        
        LogCommitJobCreated(logger, commitId, deviceId, runId);
        return commitJob;
    }

    public async Task<(bool Claimed, CommitJob CommitJob)> TryClaimCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("TryClaimCommitJob");
        var stateKey = GetCommitJobStateKey(commitId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag("commit_id", commitId.ToString());

        var (commitJob, etag) = await daprClient.GetStateAndETagAsync<CommitJob>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (commitJob == null)
        {
            throw new InvalidOperationException($"CommitJob {commitId} not found");
        }

        commitJob.ETag = etag;

        if (commitJob.Status != CommitJobStatus.Queued)
        {
            telemetry.StateOperations.Add(1, new TagList { { "operation", "claim" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "not_queued" } });
            return (false, commitJob);
        }

        commitJob.Status = CommitJobStatus.Processing;
        commitJob.UpdatedAt = DateTimeOffset.UtcNow;

        var success = await daprClient.TrySaveStateAsync(
            DaprComponents.ManifestStateStore,
            stateKey,
            commitJob,
            etag,
            cancellationToken: cancellationToken);

        if (!success)
        {
            LogConcurrentCommitJobUpdate(logger, commitId, etag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "claim" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "conflict" } });
            return (false, commitJob);
        }

        var claimedCommitJob = await GetCommitJobAsync(commitId, cancellationToken);
        telemetry.StateOperations.Add(1, new TagList { { "operation", "claim" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "claimed" } });
        LogCommitJobClaimed(logger, commitId);

        return (true, claimedCommitJob);
    }

    public async Task<CommitJob> GetCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetCommitJob");
        var stateKey = GetCommitJobStateKey(commitId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag("commit_id", commitId.ToString());

        var (commitJob, etag) = await daprClient.GetStateAndETagAsync<CommitJob>(
            DaprComponents.ManifestStateStore, 
            stateKey,
            cancellationToken: cancellationToken);
        
        if (commitJob != null)
        {
            commitJob.ETag = etag;
            activity?.SetTag(ActivityAttributes.StateETag, etag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "found" } });
            return commitJob;
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "not_found" } });
        throw new InvalidOperationException($"CommitJob {commitId} not found");
    }

    public async Task<CommitJob> UpdateCommitJobAsync(CommitJob commitJob, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("UpdateCommitJob");
        var stateKey = GetCommitJobStateKey(commitJob.CommitId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag("commit_id", commitJob.CommitId.ToString());
        activity?.SetTag("commit_status", commitJob.Status.ToString());

        commitJob.UpdatedAt = DateTimeOffset.UtcNow;

        // If ETag is present, use optimistic concurrency control
        if (!string.IsNullOrEmpty(commitJob.ETag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                commitJob,
                commitJob.ETag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                LogConcurrentCommitJobUpdate(logger, commitJob.CommitId, commitJob.ETag);
                throw new InvalidOperationException($"Concurrent update detected for CommitJob {commitJob.CommitId}");
            }
        }
        else
        {
            // No ETag, perform unconditional update
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                commitJob,
                cancellationToken: cancellationToken);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "update" }, { "store", "manifest" }, { "entity", "commitjob" } });
        LogCommitJobUpdated(logger, commitJob.CommitId, commitJob.Status);

        return commitJob;
    }

    public async Task<CommitFileProgress?> GetCommitFileProgressAsync(Guid commitId, string uniqueFileId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetCommitFileProgress");
        var stateKey = GetCommitFileProgressStateKey(commitId, uniqueFileId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag("commit_id", commitId.ToString());
        activity?.SetTag("unique_file_id", uniqueFileId);

        var (progress, etag) = await daprClient.GetStateAndETagAsync<CommitFileProgress>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (progress != null)
        {
            progress.ETag = etag;
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "commitfile" }, { "result", "found" } });
        }
        else
        {
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "commitfile" }, { "result", "not_found" } });
        }

        return progress;
    }

    public async Task<CommitFileProgress> SaveCommitFileProgressAsync(CommitFileProgress progress, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("SaveCommitFileProgress");
        var stateKey = GetCommitFileProgressStateKey(progress.CommitId, progress.UniqueFileId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag("commit_id", progress.CommitId.ToString());
        activity?.SetTag("unique_file_id", progress.UniqueFileId);
        activity?.SetTag("commit_file_status", progress.Status.ToString());

        progress.UpdatedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrEmpty(progress.ETag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                progress,
                progress.ETag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                LogConcurrentCommitFileUpdate(logger, progress.CommitId, progress.UniqueFileId, progress.ETag);
                throw new InvalidOperationException($"Concurrent update detected for CommitFileProgress {progress.CommitId}/{progress.UniqueFileId}");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                progress,
                cancellationToken: cancellationToken);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "commitfile" } });
        LogCommitFileProgressSaved(logger, progress.CommitId, progress.UniqueFileId, progress.Status);

        return progress;
    }

    public async Task<FileEntry?> GetFileEntryAsync(Guid deviceId, string relativePath, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetFileEntry");
        var stateKey = $"{deviceId}/files/{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(relativePath))}";

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag("relative_path", relativePath);

        var (fileEntry, etag) = await daprClient.GetStateAndETagAsync<FileEntry>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (fileEntry != null)
        {
            fileEntry.ETag = etag;
            activity?.SetTag(ActivityAttributes.StateETag, etag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileentry" }, { "result", "found" } });
        }
        else
        {
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileentry" }, { "result", "not_found" } });
        }

        return fileEntry;
    }

    public async Task SaveFileEntryAsync(FileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("SaveFileEntry");
        var stateKey = $"{fileEntry.DeviceId}/files/{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(fileEntry.RelativePath))}";

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, fileEntry.DeviceId);
        activity?.SetTag("relative_path", fileEntry.RelativePath);

        if (!string.IsNullOrEmpty(fileEntry.ETag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                fileEntry,
                fileEntry.ETag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                LogConcurrentFileEntryUpdate(logger, fileEntry.RelativePath, fileEntry.DeviceId, fileEntry.ETag);
                throw new InvalidOperationException($"Concurrent update detected for FileEntry {fileEntry.RelativePath}");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                fileEntry,
                cancellationToken: cancellationToken);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "fileentry" } });
        await AddFileEntryToIndexAsync(fileEntry.DeviceId, fileEntry.RelativePath, cancellationToken);
        LogFileEntrySaved(logger, fileEntry.RelativePath, fileEntry.DeviceId);
    }

    public async Task<FileVersion?> GetFileVersionAsync(Guid deviceId, string uniqueFileId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetFileVersion");
        var stateKey = $"{deviceId}/versions/{uniqueFileId}";

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag("unique_file_id", uniqueFileId);

        var (fileVersion, etag) = await daprClient.GetStateAndETagAsync<FileVersion>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (fileVersion != null)
        {
            fileVersion.ETag = etag;
            activity?.SetTag(ActivityAttributes.StateETag, etag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileversion" }, { "result", "found" } });
        }
        else
        {
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileversion" }, { "result", "not_found" } });
        }

        return fileVersion;
    }

    public async Task SaveFileVersionAsync(FileVersion fileVersion, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("SaveFileVersion");
        var stateKey = $"{fileVersion.DeviceId}/versions/{fileVersion.UniqueFileId}";

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, fileVersion.DeviceId);
        activity?.SetTag("unique_file_id", fileVersion.UniqueFileId);

        if (!string.IsNullOrEmpty(fileVersion.ETag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                fileVersion,
                fileVersion.ETag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                LogConcurrentFileVersionUpdate(logger, fileVersion.UniqueFileId, fileVersion.DeviceId, fileVersion.ETag);
                throw new InvalidOperationException($"Concurrent update detected for FileVersion {fileVersion.UniqueFileId}");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                fileVersion,
                cancellationToken: cancellationToken);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "fileversion" } });
        LogFileVersionSaved(logger, fileVersion.UniqueFileId, fileVersion.DeviceId);
    }

    public async Task<List<FileEntry>> GetAllFileEntriesAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var entries = new List<FileEntry>();
        string? continuationToken = null;

        do
        {
            var page = await GetFileEntriesPageAsync(deviceId, pageSize: 500, continuationToken, cancellationToken);
            entries.AddRange(page.Entries);
            continuationToken = page.NextContinuationToken;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));

        return entries;
    }

    public async Task<FileEntryPage> GetFileEntriesPageAsync(Guid deviceId, int pageSize, string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetAllFileEntries");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag("state.page_size", pageSize);

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than zero.");
        }

        var indexKey = GetFileEntryIndexKey(deviceId);
        var index = await daprClient.GetStateAsync<FileEntryIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        if (index == null || index.RelativePaths.Count == 0)
        {
            LogFileEntriesQueried(logger, deviceId);
            return new FileEntryPage
            {
                Entries = [],
                PageSize = pageSize,
                ContinuationToken = continuationToken,
                NextContinuationToken = null
            };
        }

        var offset = DecodeContinuationToken(continuationToken);
        if (offset >= index.RelativePaths.Count)
        {
            LogFileEntriesQueried(logger, deviceId);
            return new FileEntryPage
            {
                Entries = [],
                PageSize = pageSize,
                ContinuationToken = continuationToken,
                NextContinuationToken = null
            };
        }

        var relativePaths = index.RelativePaths
            .Order(StringComparer.OrdinalIgnoreCase)
            .Skip(offset)
            .Take(pageSize)
            .ToArray();

        var entries = new List<FileEntry>(relativePaths.Length);
        foreach (var relativePath in relativePaths)
        {
            var entry = await GetFileEntryAsync(deviceId, relativePath, cancellationToken);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        var nextOffset = offset + relativePaths.Length;
        LogFileEntriesQueried(logger, deviceId);
        return new FileEntryPage
        {
            Entries = entries,
            PageSize = pageSize,
            ContinuationToken = continuationToken,
            NextContinuationToken = nextOffset < index.RelativePaths.Count ? EncodeContinuationToken(nextOffset) : null
        };
    }

    private async Task AddFileEntryToIndexAsync(Guid deviceId, string relativePath, CancellationToken cancellationToken)
    {
        var indexKey = GetFileEntryIndexKey(deviceId);
        var (index, etag) = await daprClient.GetStateAndETagAsync<FileEntryIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        index ??= new FileEntryIndex
        {
            DeviceId = deviceId,
            RelativePaths = []
        };

        if (index.RelativePaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        index.RelativePaths.Add(relativePath);
        index.RelativePaths.Sort(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(etag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                etag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                throw new InvalidOperationException($"Concurrent update detected for file entry index of device {deviceId}");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                cancellationToken: cancellationToken);
        }
    }

    private static string GetFileEntryIndexKey(Guid deviceId) => $"{deviceId}/files/_index";

    private static string EncodeContinuationToken(int offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static int DecodeContinuationToken(string? continuationToken)
    {
        if (string.IsNullOrWhiteSpace(continuationToken))
        {
            return 0;
        }

        try
        {
            var tokenBytes = Convert.FromBase64String(continuationToken);
            var tokenText = Encoding.UTF8.GetString(tokenBytes);
            if (int.TryParse(tokenText, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0)
            {
                return offset;
            }
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("Invalid continuation token.", nameof(continuationToken));
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Updated backup run {runId} for device {deviceId} with status {status}")]
    static partial void LogBackupRunUpdated(ILogger logger, Guid runId, Guid deviceId, BackupRunStatus status);

    [LoggerMessage(LogLevel.Information, "Created backup run {runId} for device {deviceId}")]
    static partial void LogBackupRunCreated(ILogger logger, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for backup run {runId} of device {deviceId}. ETag: {etag}")]
    static partial void LogConcurrentUpdateDetected(ILogger logger, Guid runId, Guid deviceId, string etag);

    [LoggerMessage(LogLevel.Information, "Committed backup run {runId} for device {deviceId} with status {status}")]
    static partial void LogBackupRunCommitted(ILogger logger, Guid runId, Guid deviceId, BackupRunStatus status);
    [LoggerMessage(LogLevel.Information, "Created commit job {commitId} for device {deviceId}, run {runId}")]
    static partial void LogCommitJobCreated(ILogger logger, Guid commitId, Guid deviceId, Guid runId);

    [LoggerMessage(LogLevel.Information, "Commit job {commitId} already exists for device {deviceId}, run {runId} with status {status}")]
    static partial void LogCommitJobAlreadyExists(ILogger logger, Guid commitId, Guid deviceId, Guid runId, CommitJobStatus status);

    [LoggerMessage(LogLevel.Information, "Claimed commit job {commitId} for processing")]
    static partial void LogCommitJobClaimed(ILogger logger, Guid commitId);

    [LoggerMessage(LogLevel.Information, "Updated commit job {commitId} with status {status}")]
    static partial void LogCommitJobUpdated(ILogger logger, Guid commitId, CommitJobStatus status);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for commit job {commitId}. ETag: {etag}")]
    static partial void LogConcurrentCommitJobUpdate(ILogger logger, Guid commitId, string etag);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for commit file progress {commitId}/{uniqueFileId}. ETag: {etag}")]
    static partial void LogConcurrentCommitFileUpdate(ILogger logger, Guid commitId, string uniqueFileId, string etag);

    [LoggerMessage(LogLevel.Debug, "Saved commit file progress {commitId}/{uniqueFileId} with status {status}")]
    static partial void LogCommitFileProgressSaved(ILogger logger, Guid commitId, string uniqueFileId, CommitFileStatus status);

    [LoggerMessage(LogLevel.Information, "Saved file entry for path {relativePath} on device {deviceId}")]
    static partial void LogFileEntrySaved(ILogger logger, string relativePath, Guid deviceId);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for file entry {relativePath} on device {deviceId}. ETag: {etag}")]
    static partial void LogConcurrentFileEntryUpdate(ILogger logger, string relativePath, Guid deviceId, string etag);

    [LoggerMessage(LogLevel.Information, "Saved file version {uniqueFileId} on device {deviceId}")]
    static partial void LogFileVersionSaved(ILogger logger, string uniqueFileId, Guid deviceId);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for file version {uniqueFileId} on device {deviceId}. ETag: {etag}")]
    static partial void LogConcurrentFileVersionUpdate(ILogger logger, string uniqueFileId, Guid deviceId, string etag);

    [LoggerMessage(LogLevel.Information, "Queried all file entries for device {deviceId}")]
    static partial void LogFileEntriesQueried(ILogger logger, Guid deviceId);

    private static string GetCommitJobStateKey(Guid commitId) => $"commitjobs/{commitId}";

    private static string GetCommitFileProgressStateKey(Guid commitId, string uniqueFileId) => $"commitjobs/{commitId}/files/{uniqueFileId}";

    private static Guid CreateDeterministicCommitId(Guid deviceId, Guid runId)
    {
        var input = Encoding.UTF8.GetBytes($"{deviceId:N}:{runId:N}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        return new Guid(guidBytes);
    }
    #endregion
}