using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Models.Api.Requests;
using FlorisDeV.BackupApi.Models.Api.Responses;
using FlorisDeV.BackupApi.Models.Application;
using FlorisDeV.BackupApi.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupApi.Controllers;

[ApiController]
[Route("backup")]
public class BackupController(
    IBackupRunService backupRunService,
    ILogger<BackupController> logger
) : ControllerBase
{
    [HttpPost("start-run")]
    [ProducesResponseType(typeof(StartBackupRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StartBackupRunResponse>> StartBackupRun(
        [FromBody] StartBackupRunRequest request,
        CancellationToken cancellationToken
    )
    {
        // Validate request
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // Start backup-run
        BackupRunStartResult result;
        try
        {
            result = await backupRunService.StartBackupRunAsync(request.DeviceId, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error starting backup run for device {DeviceId}", request.DeviceId);

            var errorResponse = new ErrorResponse
            {
                Error = "An error occurred while starting the backup run.",
                Details = e.Message
            };
            return StatusCode(500, errorResponse);
        }

        var response = new StartBackupRunResponse()
        {
            DeviceId = result.Run.DeviceId,
            RunId = result.Run.RunId,
            StartedAt = result.Run.StartedAt,
            Status = result.Run.Status,
            SasUrlInfo = result.SasUrl
        };

        return Ok(response);
    }

    [HttpPost("commit-run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CommitBackupRun(
        [FromBody] CommitBackupRunRequest request,
        CancellationToken cancellationToken)
    {
        // Validate request
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // Commit backup-run
        try
        {
            await backupRunService.CommitBackupRunAsync(request.DeviceId, request.RunId, cancellationToken);
        }
        catch (BackupRunNotFoundException ex)
        {
            logger.LogWarning(ex, "Backup run {RunId} not found for device {DeviceId}", request.RunId, request.DeviceId);

            var errorResponse = new ErrorResponse
            {
                Error = "Backup run not found",
                Details = ex.Message
            };
            return NotFound(errorResponse);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error committing backup run {RunId} for device {DeviceId}", request.RunId, request.DeviceId);

            var errorResponse = new ErrorResponse
            {
                Error = "An error occurred while committing the backup run.",
                Details = e.Message
            };
            return StatusCode(500, errorResponse);
        }

        return Ok();
    }
}