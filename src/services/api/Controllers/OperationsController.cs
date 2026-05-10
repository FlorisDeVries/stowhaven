using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupApi.Controllers;

[ApiController]
[Route("api/ops")]
public sealed class OperationsController(IOperationalService operationalService) : ControllerBase
{
    [HttpGet("manifests")]
    [ProducesResponseType(typeof(ListManifestsResponse), StatusCodes.Status200OK)]
    public Task<ListManifestsResponse> ListManifests(
        [FromQuery] Guid? deviceId = null,
        [FromQuery] DateTimeOffset? startedFromUtc = null,
        [FromQuery] DateTimeOffset? startedToUtc = null,
        [FromQuery] BackupRunStatus? status = null,
        [FromQuery] int pageSize = 100,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
        => operationalService.ListManifestsAsync(new BackupRunQuery
        {
            DeviceId = deviceId,
            StartedFromUtc = startedFromUtc,
            StartedToUtc = startedToUtc,
            Status = status,
            PageSize = pageSize,
            ContinuationToken = continuationToken
        }, cancellationToken);

    [HttpGet("manifests/{deviceId:guid}/{runId:guid}")]
    [ProducesResponseType(typeof(ManifestDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<ManifestDetailsResponse> GetManifestDetails(
        Guid deviceId,
        Guid runId,
        CancellationToken cancellationToken)
        => operationalService.GetManifestDetailsAsync(deviceId, runId, cancellationToken);

    [HttpGet("manifests/{deviceId:guid}/{runId:guid}/files")]
    [ProducesResponseType(typeof(ManifestFilesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<ManifestFilesResponse> ListManifestFiles(
        Guid deviceId,
        Guid runId,
        CancellationToken cancellationToken)
        => operationalService.ListManifestFilesAsync(deviceId, runId, cancellationToken);

    [HttpGet("commits")]
    [ProducesResponseType(typeof(ListCommitJobsResponse), StatusCodes.Status200OK)]
    public Task<ListCommitJobsResponse> ListCommitJobs(
        [FromQuery] Guid? deviceId = null,
        [FromQuery] Guid? runId = null,
        [FromQuery] CommitJobStatus? status = null,
        [FromQuery] DateTimeOffset? createdFromUtc = null,
        [FromQuery] DateTimeOffset? createdToUtc = null,
        [FromQuery] int pageSize = 100,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
        => operationalService.ListCommitJobsAsync(new CommitJobQuery
        {
            DeviceId = deviceId,
            RunId = runId,
            Status = status,
            CreatedFromUtc = createdFromUtc,
            CreatedToUtc = createdToUtc,
            PageSize = pageSize,
            ContinuationToken = continuationToken
        }, cancellationToken);

    [HttpGet("commits/{commitId:guid}")]
    [ProducesResponseType(typeof(CommitStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CommitStatusResponse>> GetCommitJob(Guid commitId, CancellationToken cancellationToken)
    {
        var commitJob = await operationalService.GetCommitJobAsync(commitId, cancellationToken);
        return Ok(new CommitStatusResponse
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
        });
    }

    [HttpGet("commits/{commitId:guid}/details")]
    [ProducesResponseType(typeof(CommitJobDetailsResponse), StatusCodes.Status200OK)]
    public Task<CommitJobDetailsResponse> GetCommitJobDetails(Guid commitId, CancellationToken cancellationToken)
        => operationalService.GetCommitJobDetailsAsync(commitId, cancellationToken);

    [HttpGet("commits/{commitId:guid}/files")]
    [ProducesResponseType(typeof(ListCommitFileProgressResponse), StatusCodes.Status200OK)]
    public Task<ListCommitFileProgressResponse> ListCommitFileProgress(
        Guid commitId,
        [FromQuery] int pageSize = 100,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
        => operationalService.ListCommitFileProgressAsync(commitId, pageSize, continuationToken, cancellationToken);

    [HttpPost("commits/{commitId:guid}/retry")]
    [ProducesResponseType(typeof(CommitStatusResponse), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<CommitStatusResponse>> RetryCommitJob(Guid commitId, CancellationToken cancellationToken)
    {
        var commitJob = await operationalService.RetryCommitJobAsync(commitId, cancellationToken);
        return AcceptedAtAction(nameof(GetCommitJob), new { commitId = commitJob.CommitId }, new CommitStatusResponse
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
        });
    }

    [HttpGet("cleanup/staging")]
    [ProducesResponseType(typeof(StaleStagingCleanupResult), StatusCodes.Status200OK)]
    public Task<StaleStagingCleanupResult> PreviewStaleStagingCleanup(
        [FromQuery] int olderThanHours = 24,
        [FromQuery] int maxDeletes = 500,
        CancellationToken cancellationToken = default)
        => operationalService.CleanupStaleStagingAsync(
            new StaleStagingCleanupRequest(olderThanHours, DryRun: true, maxDeletes),
            cancellationToken);

    [HttpPost("cleanup/staging")]
    [ProducesResponseType(typeof(StaleStagingCleanupResult), StatusCodes.Status200OK)]
    public Task<StaleStagingCleanupResult> RunStaleStagingCleanup(
        [FromQuery] int olderThanHours = 24,
        [FromQuery] bool dryRun = true,
        [FromQuery] int maxDeletes = 500,
        CancellationToken cancellationToken = default)
        => operationalService.CleanupStaleStagingAsync(
            new StaleStagingCleanupRequest(olderThanHours, dryRun, maxDeletes),
            cancellationToken);
}
