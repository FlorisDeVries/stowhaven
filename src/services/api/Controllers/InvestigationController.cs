using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Security.Authentication;
using FlorisDeV.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupApi.Controllers;

[ApiController]
[Route("api/ops")]
[Authorize(Policy = BackupAuthorizationPolicies.Admin)]
public sealed class InvestigationController(IInvestigationService investigation) : ControllerBase
{
    [HttpGet("devices")]
    public Task<ListDevicesResponse> ListDevices([FromQuery] string? userId = null,
        [FromQuery] DeviceRegistrationStatus? status = null, [FromQuery] Guid? deviceId = null,
        [FromQuery] int pageSize = 25, [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
        => investigation.ListDevicesAsync(User.GetTenantId(), userId, status, deviceId, pageSize, continuationToken, cancellationToken);

    [HttpGet("users")]
    public Task<ListBackupUsersResponse> ListUsers([FromQuery] int pageSize = 25,
        [FromQuery] string? continuationToken = null, CancellationToken cancellationToken = default)
        => investigation.ListUsersAsync(User.GetTenantId(), pageSize, continuationToken, cancellationToken);

    [HttpGet("devices/{deviceId:guid}")]
    public Task<DeviceRegistrationResponse> GetDevice(Guid deviceId, CancellationToken cancellationToken)
        => investigation.GetDeviceAsync(User.GetTenantId(), deviceId, cancellationToken);

    [HttpGet("devices/{deviceId:guid}/state")]
    public Task<DeviceStateResponse> GetState(Guid deviceId, CancellationToken cancellationToken)
        => investigation.GetDeviceStateAsync(User.GetTenantId(), deviceId, cancellationToken);

    [HttpGet("devices/{deviceId:guid}/files")]
    public Task<ListDeviceFilesResponse> ListFiles(Guid deviceId, [FromQuery] int pageSize = 25,
        [FromQuery] string? continuationToken = null, CancellationToken cancellationToken = default)
        => investigation.ListFilesAsync(User.GetTenantId(), deviceId, pageSize, continuationToken, cancellationToken);

    [HttpGet("devices/{deviceId:guid}/file")]
    public async Task<ActionResult<DeviceFileDetailsResponse>> GetFile(Guid deviceId, [FromQuery] string logicalPath,
        CancellationToken cancellationToken)
    {
        var result = await investigation.GetFileAsync(User.GetTenantId(), deviceId, logicalPath, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
