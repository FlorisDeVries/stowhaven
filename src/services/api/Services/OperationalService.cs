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
    Task<ManifestFilesResponse> ListManifestFilesAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default);
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
        var manifest = await GetPersistedOrBlobRunManifestAsync(commitJob.DeviceId, commitJob.RunId, cancellationToken);

        return new CommitJobDetailsResponse
        {
            Commit = ToCommitStatusResponse(commitJob),
            BackupRun = backupRun,
            Progress = progress,
            ManifestAvailable = manifest != null,
            ManifestUnavailableReason = manifest == null
                ? "Manifest payload is not available for this commit's backup run."
                : null
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
        var manifest = await GetPersistedOrBlobRunManifestAsync(deviceId, runId, cancellationToken);
        var commit = await TryGetCommitStatusAsync(CreateDeterministicCommitId(deviceId, runId), cancellationToken);

        return new ManifestDetailsResponse
        {
            Summary = summary,
            Commit = commit,
            Manifest = manifest,
            ManifestAvailable = manifest != null,
            ManifestUnavailableReason = manifest == null
                ? "Manifest payload is not available. New successful runs are persisted in manifest state before temporary blob cleanup."
                : null
        };
    }

    public async Task<ManifestFilesResponse> ListManifestFilesAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        _ = await manifestManager.GetBackupRunAsync(deviceId, runId, cancellationToken);
        var run = await GetPersistedOrBlobRunManifestAsync(deviceId, runId, cancellationToken);
        if (run == null)
        {
            throw new ManifestPayloadNotAvailableException(deviceId, runId);
        }

        return new ManifestFilesResponse
        {
            DeviceId = deviceId,
            RunId = runId,
            Files = run.Files,
            Deleted = run.Deleted,
            FileCount = run.Files.Count,
            DeletedCount = run.Deleted.Count
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

    private async Task<RunManifest?> GetPersistedOrBlobRunManifestAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken)
    {
        var manifest = await manifestManager.GetRunManifestAsync(deviceId, runId, cancellationToken);
        if (manifest != null)
        {
            return manifest;
        }

        var containerClient = await blobStorageService.GetContainerClientAsync(cancellationToken);
        var blobClient = containerClient.GetBlobClient($"runs/{deviceId:N}/{runId:N}/run-manifest.json");

        try
        {
            var content = await blobClient.DownloadContentAsync(cancellationToken);
            return JsonSerializer.Deserialize<RunManifest>(content.Value.Content.ToString(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
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
