using Azure.Storage.Blobs;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupApi.Services;

public interface IOperationalService
{
    Task<CommitJob> GetCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default);
    Task<CommitJob> RetryCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default);
    Task<ListCommitJobsResponse> ListCommitJobsAsync(CommitJobQuery query, CancellationToken cancellationToken = default);
    Task<CommitJobDetailsResponse> GetCommitJobDetailsAsync(Guid commitId, CancellationToken cancellationToken = default);
    Task<ListCommitFileProgressResponse> ListCommitFileProgressAsync(Guid commitId, int pageSize, string? continuationToken = null,
        CancellationToken cancellationToken = default);
    Task<StaleStagingCleanupResult> CleanupStaleStagingAsync(StaleStagingCleanupRequest request, CancellationToken cancellationToken = default);
    Task<ListManifestsResponse> ListManifestsAsync(BackupRunQuery query, CancellationToken cancellationToken = default);
    Task<ManifestDetailsResponse> GetManifestDetailsAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);
    Task<ManifestFilesResponse> ListManifestFilesAsync(
        Guid deviceId,
        Guid runId,
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default);
}

public sealed record StaleStagingCleanupRequest(
    int OlderThanHours = 24,
    bool DryRun = true,
    int MaxDeletes = 500);

public sealed record StaleStagingCleanupResult(
    DateTimeOffset CutoffUtc,
    bool DryRun,
    int ScannedCount,
    int EligibleCount,
    int DeletedCount,
    int SkippedActiveRunCount,
    IReadOnlyList<StaleStagingBlobResult> Blobs);

public sealed record StaleStagingBlobResult(
    string BlobName,
    DateTimeOffset? LastModifiedUtc,
    Guid? DeviceId,
    Guid? RunId,
    string Action,
    string Reason);

public partial class OperationalService(
    IManifestManager manifestManager,
    IBackupEventPublisher eventPublisher,
    IBlobStorageService blobStorageService,
    ILogger<OperationalService> logger) : IOperationalService
{
    public Task<CommitJob> GetCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default)
        => manifestManager.GetCommitJobAsync(commitId, cancellationToken);

    public async Task<CommitJob> RetryCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default)
    {
        var commitJob = await manifestManager.GetCommitJobAsync(commitId, cancellationToken);
        if (commitJob.Status != CommitJobStatus.Failed)
        {
            throw new InvalidOperationException($"Only failed commit jobs can be retried. Current status: {commitJob.Status}");
        }

        commitJob.Status = CommitJobStatus.Queued;
        commitJob.Error = null;
        commitJob.FailureCategory = null;
        commitJob.NextRetryAt = null;
        commitJob.DeadLetteredAt = null;
        commitJob.CompletedAt = null;

        commitJob = await manifestManager.UpdateCommitJobAsync(commitJob, cancellationToken);
        await eventPublisher.PublishBackupRunCommittedAsync(commitJob, cancellationToken);

        LogCommitRetryQueued(logger, commitJob.CommitId, commitJob.DeviceId, commitJob.RunId, commitJob.AttemptCount);
        return commitJob;
    }

    public async Task<ListCommitJobsResponse> ListCommitJobsAsync(CommitJobQuery query, CancellationToken cancellationToken = default)
    {
        var page = await manifestManager.GetCommitJobsPageAsync(query, cancellationToken);
        return new ListCommitJobsResponse
        {
            Commits = page.Commits.Select(ToCommitStatusResponse).ToArray(),
            PageSize = page.PageSize,
            ContinuationToken = page.ContinuationToken,
            NextContinuationToken = page.NextContinuationToken
        };
    }

    public async Task<CommitJobDetailsResponse> GetCommitJobDetailsAsync(Guid commitId, CancellationToken cancellationToken = default)
    {
        var commitJob = await manifestManager.GetCommitJobAsync(commitId, cancellationToken);
        var backupRun = await TryGetBackupRunAsync(commitJob.DeviceId, commitJob.RunId, cancellationToken);
        var progress = await GetCommitFileProgressCountsAsync(commitId, cancellationToken);

        // Only availability is reported here, so probe for it rather than reading the manifest.
        var availability = await GetManifestAvailabilityAsync(commitJob.DeviceId, commitJob.RunId, cancellationToken);

        return new CommitJobDetailsResponse
        {
            Commit = ToCommitStatusResponse(commitJob),
            BackupRun = backupRun,
            Progress = progress,
            ManifestAvailable = availability.Available,
            ManifestUnavailableReason = availability.Available
                ? null
                : "Manifest payload is not available for this commit's backup run."
        };
    }

    public async Task<ListCommitFileProgressResponse> ListCommitFileProgressAsync(
        Guid commitId,
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        var commitJob = await manifestManager.GetCommitJobAsync(commitId, cancellationToken);
        var page = await manifestManager.GetCommitFileProgressPageAsync(commitId, pageSize, continuationToken, cancellationToken);

        return new ListCommitFileProgressResponse
        {
            CommitId = commitJob.CommitId,
            DeviceId = commitJob.DeviceId,
            RunId = commitJob.RunId,
            Files = page.Files.Select(ToCommitFileProgressResponse).ToArray(),
            PageSize = page.PageSize,
            ContinuationToken = page.ContinuationToken,
            NextContinuationToken = page.NextContinuationToken
        };
    }

    public async Task<ListManifestsResponse> ListManifestsAsync(BackupRunQuery query, CancellationToken cancellationToken = default)
    {
        var page = await manifestManager.GetBackupRunsPageAsync(query, cancellationToken);
        var summaries = new List<ManifestSummaryResponse>(page.Runs.Count);

        foreach (var run in page.Runs)
        {
            summaries.Add(await ToManifestSummaryAsync(run, cancellationToken));
        }

        return new ListManifestsResponse
        {
            Manifests = summaries,
            PageSize = page.PageSize,
            ContinuationToken = page.ContinuationToken,
            NextContinuationToken = page.NextContinuationToken
        };
    }

    public async Task<ManifestDetailsResponse> GetManifestDetailsAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await manifestManager.GetBackupRunAsync(deviceId, runId, cancellationToken);
        var summary = await ToManifestSummaryAsync(run, cancellationToken);
        var availability = await GetManifestAvailabilityAsync(deviceId, runId, cancellationToken);
        var commit = await TryGetCommitStatusAsync(CreateDeterministicCommitId(deviceId, runId), cancellationToken);

        return new ManifestDetailsResponse
        {
            Summary = summary,
            Commit = commit,
            ManifestAvailable = availability.Available,
            ManifestUnavailableReason = availability.Available
                ? null
                : "Manifest payload is not available. New successful runs are persisted in manifest state before temporary blob cleanup.",
            FileCount = availability.Available ? availability.FileCount : null,
            DeletedCount = availability.Available ? availability.DeletedCount : null,
            FilesUrl = availability.Available
                ? $"/api/ops/manifests/{deviceId:D}/{runId:D}/files"
                : null
        };
    }

    public async Task<ManifestFilesResponse> ListManifestFilesAsync(
        Guid deviceId,
        Guid runId,
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        _ = await manifestManager.GetBackupRunAsync(deviceId, runId, cancellationToken);

        pageSize = Math.Clamp(pageSize, 1, MaxManifestFilesPageSize);

        var availability = await GetManifestAvailabilityAsync(deviceId, runId, cancellationToken);
        if (!availability.Available)
        {
            throw new ManifestPayloadNotAvailableException(deviceId, runId);
        }

        var cursor = ManifestFilesCursor.Decode(continuationToken);

        var (files, deleted, next) = availability.Source == ManifestSource.State
            ? await ReadStatePageAsync(deviceId, runId, pageSize, cursor, cancellationToken)
            : await ReadBlobPageAsync(deviceId, runId, pageSize, cursor, cancellationToken);

        return new ManifestFilesResponse
        {
            DeviceId = deviceId,
            RunId = runId,
            Files = files,
            Deleted = deleted,
            FileCount = availability.FileCount,
            DeletedCount = availability.DeletedCount,
            PageSize = pageSize,
            ContinuationToken = continuationToken,
            NextContinuationToken = next?.Encode()
        };
    }

    public async Task<StaleStagingCleanupResult> CleanupStaleStagingAsync(
        StaleStagingCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        var olderThanHours = Math.Max(1, request.OlderThanHours);
        var maxDeletes = Math.Max(1, request.MaxDeletes);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-olderThanHours);
        var containerClient = await blobStorageService.GetContainerClientAsync(cancellationToken);
        var results = new List<StaleStagingBlobResult>();
        var scannedCount = 0;
        var eligibleCount = 0;
        var deletedCount = 0;
        var skippedActiveRunCount = 0;

        await foreach (var blob in containerClient.GetBlobsAsync(prefix: "staging/", cancellationToken: cancellationToken))
        {
            scannedCount++;
            var lastModified = blob.Properties.LastModified;
            var (deviceId, runId) = TryParseStagingBlobName(blob.Name);

            if (lastModified == null || lastModified.Value > cutoff)
            {
                results.Add(new StaleStagingBlobResult(blob.Name, lastModified, deviceId, runId, "skip", "not_stale"));
                continue;
            }

            if (deviceId.HasValue && runId.HasValue && await IsRunActiveAsync(deviceId.Value, runId.Value, cancellationToken))
            {
                skippedActiveRunCount++;
                results.Add(new StaleStagingBlobResult(blob.Name, lastModified, deviceId, runId, "skip", "run_active"));
                continue;
            }

            eligibleCount++;
            if (request.DryRun)
            {
                results.Add(new StaleStagingBlobResult(blob.Name, lastModified, deviceId, runId, "would_delete", "stale_staging"));
                continue;
            }

            if (deletedCount >= maxDeletes)
            {
                results.Add(new StaleStagingBlobResult(blob.Name, lastModified, deviceId, runId, "skip", "max_deletes_reached"));
                continue;
            }

            await containerClient.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            deletedCount++;
            results.Add(new StaleStagingBlobResult(blob.Name, lastModified, deviceId, runId, "deleted", "stale_staging"));
        }

        LogStaleStagingCleanupCompleted(logger, scannedCount, eligibleCount, deletedCount, request.DryRun);
        return new StaleStagingCleanupResult(cutoff, request.DryRun, scannedCount, eligibleCount, deletedCount, skippedActiveRunCount, results);
    }

    private async Task<bool> IsRunActiveAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            var run = await manifestManager.GetBackupRunAsync(deviceId, runId, cancellationToken);
            return run.Status is BackupRunStatus.Queued or BackupRunStatus.Processing;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ManifestSummaryResponse> ToManifestSummaryAsync(BackupRun run, CancellationToken cancellationToken)
    {
        var commitId = CreateDeterministicCommitId(run.DeviceId, run.RunId);
        var commit = await TryGetCommitJobAsync(commitId, cancellationToken);

        return new ManifestSummaryResponse
        {
            DeviceId = run.DeviceId,
            RunId = run.RunId,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            Status = run.Status,
            FilesBackedUp = run.FilesBackedUp,
            CommitId = commit?.CommitId,
            CommitStatus = commit?.Status
        };
    }

    private async Task<BackupRun?> TryGetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            return await manifestManager.GetBackupRunAsync(deviceId, runId, cancellationToken);
        }
        catch (BackupRunNotFoundException)
        {
            return null;
        }
    }

    private async Task<CommitFileProgressCounts> GetCommitFileProgressCountsAsync(Guid commitId, CancellationToken cancellationToken)
    {
        var total = 0;
        var pending = 0;
        var moved = 0;
        var stateUpdated = 0;
        var succeeded = 0;
        var failed = 0;
        string? continuationToken = null;

        do
        {
            var page = await manifestManager.GetCommitFileProgressPageAsync(commitId, 500, continuationToken, cancellationToken);
            foreach (var file in page.Files)
            {
                total++;
                switch (file.Status)
                {
                    case CommitFileStatus.Pending:
                        pending++;
                        break;
                    case CommitFileStatus.Moved:
                        moved++;
                        break;
                    case CommitFileStatus.StateUpdated:
                        stateUpdated++;
                        break;
                    case CommitFileStatus.Succeeded:
                        succeeded++;
                        break;
                    case CommitFileStatus.Failed:
                        failed++;
                        break;
                    default:
                        break;
                }
            }

            continuationToken = page.NextContinuationToken;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));

        return new CommitFileProgressCounts
        {
            Total = total,
            Pending = pending,
            Moved = moved,
            StateUpdated = stateUpdated,
            Succeeded = succeeded,
            Failed = failed
        };
    }

    /// <summary>Upper bound on entries per page, so no response can grow unbounded.</summary>
    private const int MaxManifestFilesPageSize = 1000;

    private enum ManifestSource
    {
        None,

        /// <summary>Chunked documents in the state store: the durable record.</summary>
        State,

        /// <summary>The run's temporary blob, before it is cleaned up post-commit.</summary>
        Blob
    }

    private sealed record ManifestAvailability(bool Available, int FileCount, int DeletedCount, ManifestSource Source);

    /// <summary>
    /// Establishes whether a run's manifest can be read, and how many entries it has, without
    /// materializing it. The state store answers from a single header document; the blob fallback
    /// counts by streaming, which is bounded in memory but proportional in time to the manifest size.
    /// </summary>
    private async Task<ManifestAvailability> GetManifestAvailabilityAsync(
        Guid deviceId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var header = await manifestManager.GetRunManifestHeaderAsync(deviceId, runId, cancellationToken);
        if (header != null)
        {
            return new ManifestAvailability(true, header.EffectiveFileCount, header.EffectiveDeletedCount, ManifestSource.State);
        }

        var blobClient = await GetManifestBlobClientAsync(deviceId, runId, cancellationToken);
        if (!await blobClient.ExistsAsync(cancellationToken))
        {
            return new ManifestAvailability(false, 0, 0, ManifestSource.None);
        }

        var files = 0;
        var deletions = 0;

        await using var stream = await blobClient.OpenReadAsync(cancellationToken: cancellationToken);
        await foreach (var item in RunManifestStreamReader.ReadAsync(stream, cancellationToken))
        {
            if (item.File is not null)
            {
                files++;
            }
            else
            {
                deletions++;
            }
        }

        return new ManifestAvailability(true, files, deletions, ManifestSource.Blob);
    }

    private async Task<BlobClient> GetManifestBlobClientAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken)
    {
        var containerClient = await blobStorageService.GetContainerClientAsync(cancellationToken);
        return containerClient.GetBlobClient($"runs/{deviceId:N}/{runId:N}/run-manifest.json");
    }

    private async Task<(IReadOnlyList<ManifestFileEntry> Files, IReadOnlyList<string> Deleted, ManifestFilesCursor? Next)>
        ReadStatePageAsync(Guid deviceId, Guid runId, int pageSize, ManifestFilesCursor cursor, CancellationToken cancellationToken)
    {
        var page = await manifestManager.GetRunManifestEntryPageAsync(
            deviceId, runId, pageSize, cursor.ChunkToken, cursor.Skip, cancellationToken);

        var next = page.HasMore
            ? new ManifestFilesCursor(page.NextChunkToken, page.NextSkip, 0)
            : null;

        return (page.Files, page.Deleted, next);
    }

    /// <summary>
    /// Pages a manifest that is still only a blob by streaming past the entries already returned.
    /// Deep pages therefore re-read the prefix; acceptable because this path only applies to runs
    /// whose manifest has not been persisted to the state store yet.
    /// </summary>
    private async Task<(IReadOnlyList<ManifestFileEntry> Files, IReadOnlyList<string> Deleted, ManifestFilesCursor? Next)>
        ReadBlobPageAsync(Guid deviceId, Guid runId, int pageSize, ManifestFilesCursor cursor, CancellationToken cancellationToken)
    {
        var blobClient = await GetManifestBlobClientAsync(deviceId, runId, cancellationToken);

        var files = new List<ManifestFileEntry>();
        var deleted = new List<string>();
        var index = 0;
        var hasMore = false;

        await using var stream = await blobClient.OpenReadAsync(cancellationToken: cancellationToken);

        await foreach (var item in RunManifestStreamReader.ReadAsync(stream, cancellationToken))
        {
            if (index++ < cursor.Offset)
            {
                continue;
            }

            if (files.Count + deleted.Count == pageSize)
            {
                // One entry beyond the page proves there is more without returning it.
                hasMore = true;
                break;
            }

            if (item.File is { } file)
            {
                files.Add(file);
            }
            else if (item.DeletedPath is { } path)
            {
                deleted.Add(path);
            }
        }

        var next = hasMore
            ? new ManifestFilesCursor(null, 0, cursor.Offset + files.Count + deleted.Count)
            : null;

        return (files, deleted, next);
    }

    /// <summary>
    /// Opaque page cursor. Chunked manifests resume from a store token plus an offset inside that
    /// chunk; blob-backed manifests resume from a plain entry offset.
    /// </summary>
    private sealed record ManifestFilesCursor(string? ChunkToken, int Skip, int Offset)
    {
        public static ManifestFilesCursor Decode(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return new ManifestFilesCursor(null, 0, 0);
            }

            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                return JsonSerializer.Deserialize<ManifestFilesCursor>(json)
                    ?? throw new InvalidContinuationTokenException();
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                throw new InvalidContinuationTokenException();
            }
        }

        public string Encode()
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)));
    }

    private async Task<CommitJob?> TryGetCommitJobAsync(Guid commitId, CancellationToken cancellationToken)
    {
        try
        {
            return await manifestManager.GetCommitJobAsync(commitId, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task<CommitStatusResponse?> TryGetCommitStatusAsync(Guid commitId, CancellationToken cancellationToken)
    {
        var commitJob = await TryGetCommitJobAsync(commitId, cancellationToken);
        return commitJob == null
            ? null
            : ToCommitStatusResponse(commitJob);
    }

    private static CommitStatusResponse ToCommitStatusResponse(CommitJob commitJob) => new()
    {
        CommitId = commitJob.CommitId,
        DeviceId = commitJob.DeviceId,
        RunId = commitJob.RunId,
        Status = commitJob.Status,
        Error = commitJob.Error,
        CreatedAt = commitJob.CreatedAt,
        UpdatedAt = commitJob.UpdatedAt,
        CompletedAt = commitJob.CompletedAt,
        FilesProcessed = commitJob.FilesProcessed,
        FilesFailed = commitJob.FilesFailed,
        FailureCategory = commitJob.FailureCategory,
        AttemptCount = commitJob.AttemptCount,
        LastErrorAt = commitJob.LastErrorAt,
        NextRetryAt = commitJob.NextRetryAt,
        DeadLetteredAt = commitJob.DeadLetteredAt
    };

    private static CommitFileProgressResponse ToCommitFileProgressResponse(CommitFileProgress progress) => new()
    {
        CommitId = progress.CommitId,
        DeviceId = progress.DeviceId,
        RunId = progress.RunId,
        UniqueFileId = progress.UniqueFileId,
        LogicalPath = progress.LogicalPath,
        Status = progress.Status,
        UpdatedAt = progress.UpdatedAt,
        Error = progress.Error
    };

    private static Guid CreateDeterministicCommitId(Guid deviceId, Guid runId)
    {
        var input = Encoding.UTF8.GetBytes($"{deviceId:N}:{runId:N}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        return new Guid(guidBytes);
    }

    private static (Guid? DeviceId, Guid? RunId) TryParseStagingBlobName(string blobName)
    {
        var parts = blobName.Split('/', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !string.Equals(parts[0], "staging", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        return Guid.TryParseExact(parts[1], "N", out var deviceId) &&
               Guid.TryParseExact(parts[2], "N", out var runId)
            ? (deviceId, runId)
            : (null, null);
    }

    [LoggerMessage(LogLevel.Information, "Queued retry for commit {CommitId} device {DeviceId} run {RunId}; previous attempts {AttemptCount}")]
    static partial void LogCommitRetryQueued(ILogger logger, Guid commitId, Guid deviceId, Guid runId, int attemptCount);

    [LoggerMessage(LogLevel.Information, "Stale staging cleanup completed. scanned={ScannedCount}, eligible={EligibleCount}, deleted={DeletedCount}, dryRun={DryRun}")]
    static partial void LogStaleStagingCleanupCompleted(ILogger logger, int scannedCount, int eligibleCount, int deletedCount, bool dryRun);
}
