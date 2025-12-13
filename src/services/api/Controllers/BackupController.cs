using FlorisDeV.BackupApi.Models.Api.Requests;
using FlorisDeV.BackupApi.Models.Api.Responses;
using FlorisDeV.BackupApi.Models.Application;
using FlorisDeV.BackupApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupApi.Controllers;

[Authorize]
[ApiController]
[Route("backup")]
public class BackupController(
    IBackupRunService backupRunService
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
        // Validate request
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // Start backup-run - let exceptions bubble up to GlobalExceptionFilter
        var result = await backupRunService.StartBackupRunAsync(request.DeviceId, cancellationToken);

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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CommitBackupRun(
        [FromBody] CommitBackupRunRequest request,
        CancellationToken cancellationToken)
    {
        // Validate request
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // Commit backup-run - let exceptions bubble up to GlobalExceptionFilter
        await backupRunService.CommitBackupRunAsync(request.DeviceId, request.RunId, cancellationToken);

        return Ok();
    }
}