using Azure.Storage.Blobs;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupApi.Services;

public interface IOperationalService
{
    Task<CommitJob> GetCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default);
    Task<CommitJob> RetryCommitJobAsync(Guid commitId, CancellationToken cancellationToken = default);
    Task<StaleStagingCleanupResult> CleanupStaleStagingAsync(StaleStagingCleanupRequest request, CancellationToken cancellationToken = default);
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
