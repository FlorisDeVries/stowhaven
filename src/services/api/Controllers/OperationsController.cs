using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Responses;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupApi.Controllers;

[ApiController]
[Route("api/ops")]
public sealed class OperationsController(IOperationalService operationalService) : ControllerBase
{
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
