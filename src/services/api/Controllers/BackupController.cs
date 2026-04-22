using FlorisDeV.BackupApi.Models.Api.Requests;
using FlorisDeV.BackupApi.Models.Api.Responses;
using FlorisDeV.BackupApi.Models.State;
using FlorisDeV.BackupApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class BackupController(
    IBackupRunService backupRunService,
    ILogger<BackupController> logger
) : ControllerBase
{
    [HttpPost("start-run")]
    [ProducesResponseType(typeof(StartBackupRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StartBackupRunResponse>> StartBackupRun(
        [FromBody] StartBackupRunRequest request,
        CancellationToken cancellationToken
    )
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["DeviceId"] = request.DeviceId,
            ["Operation"] = "StartBackupRun"
        });

        // Validate request
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        LogStartingBackupRun(logger, request.DeviceId);

        // Get client IP for SAS URL restriction
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        // Start backup-run - let exceptions bubble up to GlobalExceptionFilter
        var result = await backupRunService.StartBackupRunAsync(request.DeviceId, clientIp, cancellationToken);

        var response = new StartBackupRunResponse
        {
            DeviceId = result.Run.DeviceId,
            RunId = result.Run.RunId,
            StartedAt = result.Run.StartedAt,
            Status = result.Run.Status,
            SasUrlInfo = result.SasUrl
        };

        LogBackupRunStartedSuccess(logger, result.Run.RunId, result.Run.DeviceId);

        return Ok(response);
    }

    [HttpPost("commit-run")]
    [ProducesResponseType(typeof(CommitBackupRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CommitBackupRunResponse>> CommitBackupRun(
        [FromBody] CommitBackupRunRequest request,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["DeviceId"] = request.DeviceId,
            ["Operation"] = "CommitBackupRun"
        });

        // Validate request
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        LogStartCommitBackupRun(logger, request.RunId, request.DeviceId);

        // Create commit job for async processing - let exceptions bubble up to GlobalExceptionFilter
        var commitJob = await backupRunService.CommitBackupRunAsync(
            request.DeviceId,
            request.RunId,
            request.ManifestBlobPath,
            cancellationToken);

        var response = new CommitBackupRunResponse
        {
            CommitId = commitJob.CommitId,
            DeviceId = commitJob.DeviceId,
            RunId = commitJob.RunId,
            Status = commitJob.Status,
            CreatedAt = commitJob.CreatedAt
        };

        LogCommitBackupRunAccepted(logger, commitJob.CommitId, request.RunId, request.DeviceId);

        return AcceptedAtAction(
            nameof(GetCommitStatus),
            new { commitId = commitJob.CommitId },
            response);
    }

    [HttpGet("commit-status/{commitId}")]
    [ProducesResponseType(typeof(CommitStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<CommitStatusResponse>> GetCommitStatus(
        Guid commitId,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CommitId"] = commitId,
            ["Operation"] = "GetCommitStatus"
        });

        LogGetCommitStatus(logger, commitId);

        // Get commit job status - let exceptions bubble up to GlobalExceptionFilter
        var commitJob = await backupRunService.GetCommitStatusAsync(commitId, cancellationToken);

        var response = new CommitStatusResponse
        {
            CommitId = commitJob.CommitId,
            DeviceId = commitJob.DeviceId,
            RunId = commitJob.RunId,
            Status = commitJob.Status,
            Error = commitJob.Error,
            CreatedAt = commitJob.CreatedAt,
            UpdatedAt = commitJob.UpdatedAt,
            CompletedAt = commitJob.CompletedAt,
            FilesProcessed = commitJob.Status == CommitJobStatus.Succeeded ? commitJob.FilesProcessed : null
        };

        LogCommitStatusRetrieved(logger, commitId, commitJob.Status);

        return Ok(response);
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Starting backup run for device {deviceId}")]
    static partial void LogStartingBackupRun(ILogger<BackupController> logger, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Backup run {runId} started successfully for device {deviceId}")]
    static partial void LogBackupRunStartedSuccess(ILogger<BackupController> logger,
        Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Committing backup run {runId} for device {deviceId}")]
    static partial void LogStartCommitBackupRun(ILogger<BackupController> logger, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Backup run {runId} for device {deviceId} accepted for commit. CommitId: {commitId}")]
    static partial void LogCommitBackupRunAccepted(ILogger<BackupController> logger, Guid commitId, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Retrieving commit status for commit {commitId}")]
    static partial void LogGetCommitStatus(ILogger<BackupController> logger, Guid commitId);

    [LoggerMessage(LogLevel.Information, "Commit {commitId} status: {status}")]
    static partial void LogCommitStatusRetrieved(ILogger<BackupController> logger, Guid commitId, CommitJobStatus status);

    #endregion
}