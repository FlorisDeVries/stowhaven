using System.Diagnostics;
using System.Text.Json;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Logging.OpenTelemetry;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager
{
    public static readonly TimeSpan CommitLeaseDuration = TimeSpan.FromMinutes(2);
    public async Task<CommitJob> CreateCommitJobAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("CreateCommitJob");
        var commitId = CreateDeterministicCommitId(deviceId, runId);
        var now = DateTimeOffset.UtcNow;

        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());
        activity?.SetTag("commit_id", commitId.ToString());

        var existing = await store.GetAsync<CommitJob>(
            CommitJobDocument, CommitPartition(commitId), $"{commitId:N}", cancellationToken);

        if (existing != null)
        {
            var existingCommitJob = existing.Data;
            existingCommitJob.ETag = existing.ETag;
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "duplicate" } });
            LogCommitJobAlreadyExists(logger, commitId, deviceId, runId, existingCommitJob.Status);
            return existingCommitJob;
        }

        var run = await store.GetAsync<BackupRun>(BackupRunDocument, DevicePartition(deviceId), $"{runId:N}", cancellationToken);
        var commitJob = new CommitJob
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            RunStartedAt = run?.Data.StartedAt ?? now,
            Status = CommitJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };

        commitJob.ETag = await store.TryCreateAsync(
            CommitJobDocument, CommitPartition(commitId), $"{commitId:N}", commitJob,
            sortValue: commitJob.CreatedAt.ToUnixTimeMilliseconds(), cancellationToken: cancellationToken);
        if (commitJob.ETag == null)
        {
            // Another request won after our initial read. It may already be processing; return
            // its current state instead of replacing it with our stale Queued candidate.
            return await GetCommitJobAsync(commitId, cancellationToken);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "create" }, { "store", "manifest" }, { "entity", "commitjob" } });

        LogCommitJobCreated(logger, commitId, deviceId, runId);
        return commitJob;
    }

    public async Task<(bool Claimed, CommitJob CommitJob)> TryClaimCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("TryClaimCommitJob");
        activity?.SetTag("commit_id", commitId.ToString());

        var commitJob = await GetCommitJobAsync(commitId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var canClaim = commitJob.Status switch
        {
            CommitJobStatus.Queued => true,
            // The UpdatedAt fallback lets jobs left by an older worker recover after an upgrade.
            CommitJobStatus.Processing => (commitJob.LeaseExpiresAt ?? commitJob.UpdatedAt.Add(_commitLeaseDuration)) <= now,
            CommitJobStatus.Failed => commitJob.DeadLetteredAt == null && commitJob.NextRetryAt is { } retryAt && retryAt <= now,
            _ => false
        };
        if (!canClaim)
        {
            telemetry.StateOperations.Add(1, new TagList { { "operation", "claim" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "not_queued" } });
            return (false, commitJob);
        }

        var expectedETag = commitJob.ETag;
        if (commitJob.RunStartedAt == default)
        {
            var run = await store.GetAsync<BackupRun>(BackupRunDocument, DevicePartition(commitJob.DeviceId),
                $"{commitJob.RunId:N}", cancellationToken);
            commitJob.RunStartedAt = run?.Data.StartedAt ?? commitJob.CreatedAt;
        }
        commitJob.ClaimId = Guid.NewGuid();
        var deviceClaim = await TryAcquireDeviceLeaseAsync(commitJob, cancellationToken);
        if (!deviceClaim.Acquired)
        {
            if (deviceClaim.Superseded)
            {
                commitJob.Status = CommitJobStatus.Failed;
                commitJob.FailureCategory = "Superseded";
                commitJob.Error = "A newer backup run has already completed for this device.";
                commitJob.DeadLetteredAt = now;
                commitJob.CompletedAt = now;
                commitJob.NextRetryAt = null;
                commitJob.LeaseExpiresAt = null;
                commitJob.ClaimId = null;
                try { commitJob.ETag = await SaveCommitJobAsync(commitJob, expectedETag, cancellationToken); }
                catch (StateConcurrencyException) { commitJob = await GetCommitJobAsync(commitId, cancellationToken); }
            }
            return (false, commitJob);
        }
        commitJob.Status = CommitJobStatus.Processing;
        commitJob.AttemptCount++;
        commitJob.UpdatedAt = now;
        commitJob.LeaseExpiresAt = now.Add(_commitLeaseDuration);
        commitJob.CompletedAt = null;
        commitJob.NextRetryAt = null;
        commitJob.Error = null;

        try
        {
            commitJob.ETag = await SaveCommitJobAsync(commitJob, expectedETag, cancellationToken);
        }
        catch (StateConcurrencyException)
        {
            await UpdateDeviceLeaseAsync(commitJob, release: true, cancellationToken);
            LogConcurrentCommitJobUpdate(logger, commitId, expectedETag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "claim" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "conflict" } });
            return (false, commitJob);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "claim" }, { "store", "manifest" }, { "entity", "commitjob" }, { "result", "claimed" } });
        LogCommitJobClaimed(logger, commitId);

        return (true, commitJob);
    }

    public async Task<CommitJob> GetCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetCommitJob");
        activity?.SetTag("commit_id", commitId.ToString());

        var document = await store.GetAsync<CommitJob>(
            CommitJobDocument, CommitPartition(commitId), $"{commitId:N}", cancellationToken);

        if (document != null)
        {
            var commitJob = document.Data;
            commitJob.ETag = document.ETag;
            activity?.SetTag(ActivityAttributes.StateETag, document.ETag);
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
        activity?.SetTag("state.page_size", pageSize);

        var fieldFilters = new List<DocumentFieldEquals>(2);
        if (query.DeviceId.HasValue)
        {
            fieldFilters.Add(new DocumentFieldEquals("deviceId", FormatGuid(query.DeviceId.Value)));
        }

        if (query.RunId.HasValue)
        {
            fieldFilters.Add(new DocumentFieldEquals("runId", FormatGuid(query.RunId.Value)));
        }

        var page = await store.QueryAsync<CommitJob>(new DocumentQuery
        {
            Type = CommitJobDocument,
            FieldEquals = fieldFilters,
            SortValueFrom = query.CreatedFromUtc?.ToUnixTimeMilliseconds(),
            SortValueTo = query.CreatedToUtc?.ToUnixTimeMilliseconds(),
            Order = DocumentOrder.SortValueDescending,
            PageSize = pageSize,
            ContinuationToken = query.ContinuationToken
        }, cancellationToken);

        var commits = new List<CommitJob>(page.Items.Count);
        foreach (var document in page.Items)
        {
            var commit = document.Data;
            commit.ETag = document.ETag;

            // The status filter is applied per page: filtered pages may contain fewer
            // than pageSize items while a continuation token is still present.
            if (!query.Status.HasValue || commit.Status == query.Status.Value)
            {
                commits.Add(commit);
            }
        }

        return new CommitJobPage
        {
            Commits = commits,
            PageSize = pageSize,
            ContinuationToken = query.ContinuationToken,
            NextContinuationToken = page.NextContinuationToken
        };
    }

    public async Task<CommitJob> UpdateCommitJobAsync(CommitJob commitJob, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("UpdateCommitJob");
        activity?.SetTag("commit_id", commitJob.CommitId.ToString());
        activity?.SetTag("commit_status", commitJob.Status.ToString());

        var expectedETag = commitJob.ETag;
        commitJob.UpdatedAt = DateTimeOffset.UtcNow;
        commitJob.LeaseExpiresAt = commitJob.Status == CommitJobStatus.Processing
            ? commitJob.UpdatedAt.Add(_commitLeaseDuration)
            : null;

        try
        {
            // Renew before publishing progress. Release only after file mutations are complete;
            // terminal publication can then be retried without overlapping catalog operations.
            if (commitJob.ClaimId != null && commitJob.Status != CommitJobStatus.Processing)
            {
                var current = await GetCommitJobAsync(commitJob.CommitId, cancellationToken);
                if (current.ETag != expectedETag) throw new StateConcurrencyException(CommitJobDocument, $"{commitJob.CommitId:N}");
            }
            await UpdateDeviceLeaseAsync(commitJob, release: commitJob.Status != CommitJobStatus.Processing, cancellationToken);
            commitJob.ETag = await SaveCommitJobAsync(commitJob, expectedETag, cancellationToken);
        }
        catch (StateConcurrencyException)
        {
            LogConcurrentCommitJobUpdate(logger, commitJob.CommitId, expectedETag);
            throw new InvalidOperationException($"Concurrent update detected for CommitJob {commitJob.CommitId}");
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "update" }, { "store", "manifest" }, { "entity", "commitjob" } });
        LogCommitJobUpdated(logger, commitJob.CommitId, commitJob.Status);

        return commitJob;
    }

    public async Task<CommitFileProgress?> GetCommitFileProgressAsync(Guid commitId, string uniqueFileId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetCommitFileProgress");
        activity?.SetTag("commit_id", commitId.ToString());
        activity?.SetTag("unique_file_id", uniqueFileId);

        var document = await store.GetAsync<CommitFileProgress>(
            CommitFileProgressDocument, CommitPartition(commitId), EncodeStateKeySegment(uniqueFileId), cancellationToken);

        if (document != null)
        {
            var progress = document.Data;
            progress.ETag = document.ETag;
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "commitfile" }, { "result", "found" } });
            return progress;
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "commitfile" }, { "result", "not_found" } });
        return null;
    }

    public async Task<CommitFileProgress> SaveCommitFileProgressAsync(CommitFileProgress progress, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("SaveCommitFileProgress");
        activity?.SetTag("commit_id", progress.CommitId.ToString());
        activity?.SetTag("unique_file_id", progress.UniqueFileId);
        activity?.SetTag("commit_file_status", progress.Status.ToString());

        var expectedETag = progress.ETag;
        progress.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            progress.ETag = await store.UpsertAsync(
                CommitFileProgressDocument,
                CommitPartition(progress.CommitId),
                EncodeStateKeySegment(progress.UniqueFileId),
                progress,
                expectedETag,
                sortKey: progress.UniqueFileId.ToLowerInvariant(),
                cancellationToken: cancellationToken);
        }
        catch (StateConcurrencyException)
        {
            LogConcurrentCommitFileUpdate(logger, progress.CommitId, progress.UniqueFileId, expectedETag);
            throw new InvalidOperationException($"Concurrent update detected for CommitFileProgress {progress.CommitId}/{progress.UniqueFileId}");
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "commitfile" } });
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

        activity?.SetTag("commit_id", commitId.ToString());
        activity?.SetTag("state.page_size", normalizedPageSize);

        var page = await store.QueryAsync<CommitFileProgress>(new DocumentQuery
        {
            Type = CommitFileProgressDocument,
            PartitionKey = CommitPartition(commitId),
            Order = DocumentOrder.SortKeyAscending,
            PageSize = normalizedPageSize,
            ContinuationToken = continuationToken
        }, cancellationToken);

        var files = new List<CommitFileProgress>(page.Items.Count);
        foreach (var document in page.Items)
        {
            var progress = document.Data;
            progress.ETag = document.ETag;
            files.Add(progress);
        }

        return new CommitFileProgressPage
        {
            Files = files,
            PageSize = normalizedPageSize,
            ContinuationToken = continuationToken,
            NextContinuationToken = page.NextContinuationToken
        };
    }

    public async Task<CommitFileProgressPage> GetCommitFileProgressByStatusPageAsync(
        Guid commitId,
        CommitFileStatus status,
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetCommitFileProgressByStatusPage");
        var normalizedPageSize = Math.Clamp(pageSize, 1, 500);

        activity?.SetTag("commit_id", commitId.ToString());
        activity?.SetTag("commit_file_status", status.ToString());
        activity?.SetTag("state.page_size", normalizedPageSize);

        var page = await store.QueryAsync<CommitFileProgress>(new DocumentQuery
        {
            Type = CommitFileProgressDocument,
            PartitionKey = CommitPartition(commitId),
            FieldEquals =
            [
                new DocumentFieldEquals(
                    "status",
                    JsonNamingPolicy.CamelCase.ConvertName(status.ToString()))
            ],
            PageSize = normalizedPageSize,
            ContinuationToken = continuationToken
        }, cancellationToken);

        var files = new List<CommitFileProgress>(page.Items.Count);
        foreach (var document in page.Items)
        {
            var progress = document.Data;
            progress.ETag = document.ETag;
            files.Add(progress);
        }

        return new CommitFileProgressPage
        {
            Files = files,
            PageSize = normalizedPageSize,
            ContinuationToken = continuationToken,
            NextContinuationToken = page.NextContinuationToken
        };
    }

    private Task<string> SaveCommitJobAsync(CommitJob commitJob, string? etag, CancellationToken cancellationToken)
        => store.UpsertAsync(
            CommitJobDocument,
            CommitPartition(commitJob.CommitId),
            $"{commitJob.CommitId:N}",
            commitJob,
            etag,
            sortValue: commitJob.CreatedAt.ToUnixTimeMilliseconds(),
            cancellationToken: cancellationToken);
}
