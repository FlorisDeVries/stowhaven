using FlorisDeV.BackupApi.Models.Api.Requests;
using FlorisDeV.BackupApi.Models.Api.Responses;
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

        // Start backup-run - let exceptions bubble up to GlobalExceptionFilter
        var result = await backupRunService.StartBackupRunAsync(request.DeviceId, cancellationToken);

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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CommitBackupRun(
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

        // Commit backup-run - let exceptions bubble up to GlobalExceptionFilter
        await backupRunService.CommitBackupRunAsync(request.DeviceId, request.RunId, cancellationToken);

        LogCommitBackupRunSuccess(logger, request.RunId, request.DeviceId);

        return Ok();
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Starting backup run for device {deviceId}")]
    static partial void LogStartingBackupRun(ILogger<BackupController> logger, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Backup run {runId} started successfully for device {deviceId}")]
    static partial void LogBackupRunStartedSuccess(ILogger<BackupController> logger,
        Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Committing backup run {runId} for device {deviceId}")]
    static partial void LogStartCommitBackupRun(ILogger<BackupController> logger, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Backup run {runId} committed successfully for device {deviceId}")]
    static partial void LogCommitBackupRunSuccess(ILogger<BackupController> logger, Guid runId, Guid deviceId);

    #endregion
}