using FlorisDeV.BackupApi.Options;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupApi.Controllers;

[Authorize]
[ApiController]
public class RestoreController(
    IRestoreService restoreService,
    IDeviceAuthorizationService deviceAuthorizationService,
    IOptions<SasSecurityOptions> sasSecurityOptions) : ControllerBase
{
    [HttpGet("/api/devices/{deviceId:guid}/restore/files")]
    [ProducesResponseType(typeof(ListRestoreFilesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ListRestoreFilesResponse>> ListRestoreFiles(
        Guid deviceId,
        [FromQuery] int pageSize = RestoreService.DefaultPageSize,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        await deviceAuthorizationService.AuthorizeDeviceAsync(User, deviceId, cancellationToken);
        var response = await restoreService.ListRestoreFilesAsync(deviceId, pageSize, continuationToken, cancellationToken);
        return Ok(response);
    }

    [HttpPost("/api/devices/{deviceId:guid}/restore/start")]
    [ProducesResponseType(typeof(StartRestoreResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StartRestoreResponse>> StartRestore(
        Guid deviceId,
        [FromBody] StartRestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        await deviceAuthorizationService.AuthorizeDeviceAsync(User, deviceId, cancellationToken);

        var clientIp = sasSecurityOptions.Value.EnableIpRestriction
            ? HttpContext.Connection.RemoteIpAddress?.ToString()
            : null;

        var response = await restoreService.StartRestoreAsync(deviceId, request, clientIp, cancellationToken);
        return Ok(response);
    }
}
