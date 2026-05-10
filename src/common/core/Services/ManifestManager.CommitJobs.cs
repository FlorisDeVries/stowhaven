using System.Diagnostics;
using FlorisDeV.BackupContracts.Constants;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Logging.OpenTelemetry;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager
{
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
            await AddCommitJobToIndexesAsync(existingCommitJob, cancellationToken);
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

        await AddCommitJobToIndexesAsync(commitJob, cancellationToken);

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
        commitJob.AttemptCount++;
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

    public async Task<CommitJobPage> GetCommitJobsPageAsync(CommitJobQuery query, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetCommitJobsPage");
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var indexKey = query.DeviceId.HasValue && query.RunId.HasValue
            ? GetCommitJobRunIndexKey(query.DeviceId.Value, query.RunId.Value)
            : query.DeviceId.HasValue
                ? GetCommitJobDeviceIndexKey(query.DeviceId.Value)
                : GetCommitJobGlobalIndexKey();

        activity?.SetTag(ActivityAttributes.StateKey, indexKey);
        activity?.SetTag("state.page_size", pageSize);

        var index = await daprClient.GetStateAsync<CommitJobIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        if (index == null || index.Commits.Count == 0)
        {
            return new CommitJobPage
            {
                Commits = [],
                PageSize = pageSize,
                ContinuationToken = query.ContinuationToken,
                NextContinuationToken = null
            };
        }

        var indexedCommits = index.Commits
            .Where(commit => !query.DeviceId.HasValue || commit.DeviceId == query.DeviceId.Value)
            .Where(commit => !query.RunId.HasValue || commit.RunId == query.RunId.Value)
            .Where(commit => !query.CreatedFromUtc.HasValue || commit.CreatedAt >= query.CreatedFromUtc.Value)
            .Where(commit => !query.CreatedToUtc.HasValue || commit.CreatedAt <= query.CreatedToUtc.Value)
            .OrderByDescending(commit => commit.CreatedAt)
            .ThenBy(commit => commit.CommitId)
            .ToArray();

        var commits = new List<CommitJob>(indexedCommits.Length);
        foreach (var indexedCommit in indexedCommits)
        {
            try
            {
                var commit = await GetCommitJobAsync(indexedCommit.CommitId, cancellationToken);
                if (!query.Status.HasValue || commit.Status == query.Status.Value)
                {
                    commits.Add(commit);
                }
            }
            catch (InvalidOperationException)
            {
                // Keep listing resilient if an index entry points to a removed state item.
            }
        }

        var offset = DecodeContinuationToken(query.ContinuationToken);
        var pageCommits = commits
            .Skip(offset)
            .Take(pageSize)
            .ToArray();

        var nextOffset = offset + pageCommits.Length;
        return new CommitJobPage
        {
            Commits = pageCommits,
            PageSize = pageSize,
            ContinuationToken = query.ContinuationToken,
            NextContinuationToken = nextOffset < commits.Count ? EncodeContinuationToken(nextOffset) : null
        };
    }

    public async Task<CommitJob> UpdateCommitJobAsync(CommitJob commitJob, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("UpdateCommitJob");
        var stateKey = GetCommitJobStateKey(commitJob.CommitId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag("commit_id", commitJob.CommitId.ToString());
        activity?.SetTag("commit_status", commitJob.Status.ToString());

        commitJob.UpdatedAt = DateTimeOffset.UtcNow;

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
        await AddCommitFileProgressToIndexAsync(progress, cancellationToken);
        LogCommitFileProgressSaved(logger, progress.CommitId, progress.UniqueFileId, progress.Status);

        return progress;
    }

    public async Task<CommitFileProgressPage> GetCommitFileProgressPageAsync(
        Guid commitId,
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetCommitFileProgressPage");
        var normalizedPageSize = Math.Clamp(pageSize, 1, 500);
        var indexKey = GetCommitFileProgressIndexKey(commitId);

        activity?.SetTag(ActivityAttributes.StateKey, indexKey);
        activity?.SetTag("commit_id", commitId.ToString());
        activity?.SetTag("state.page_size", normalizedPageSize);

        var index = await daprClient.GetStateAsync<CommitFileProgressIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        if (index == null || index.UniqueFileIds.Count == 0)
        {
            return new CommitFileProgressPage
            {
                Files = [],
                PageSize = normalizedPageSize,
                ContinuationToken = continuationToken,
                NextContinuationToken = null
            };
        }

        var offset = DecodeContinuationToken(continuationToken);
        var uniqueFileIds = index.UniqueFileIds
            .Order(StringComparer.OrdinalIgnoreCase)
            .Skip(offset)
            .Take(normalizedPageSize)
            .ToArray();

        var files = new List<CommitFileProgress>(uniqueFileIds.Length);
        foreach (var uniqueFileId in uniqueFileIds)
        {
            var progress = await GetCommitFileProgressAsync(commitId, uniqueFileId, cancellationToken);
            if (progress != null)
            {
                files.Add(progress);
            }
        }

        var nextOffset = offset + uniqueFileIds.Length;
        return new CommitFileProgressPage
        {
            Files = files,
            PageSize = normalizedPageSize,
            ContinuationToken = continuationToken,
            NextContinuationToken = nextOffset < index.UniqueFileIds.Count ? EncodeContinuationToken(nextOffset) : null
        };
    }
}
