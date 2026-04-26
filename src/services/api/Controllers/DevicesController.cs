using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupApi.Controllers;

[Authorize]
[ApiController]
[Route("api/devices")]
public sealed class DevicesController(IDeviceRegistryService deviceRegistryService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(DeviceRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(DeviceRegistrationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DeviceRegistrationResponse>> RegisterDevice(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var registration = await deviceRegistryService.RegisterDeviceAsync(
            User,
            request.DeviceId,
            request.DisplayName,
            cancellationToken);

        var response = ToResponse(registration);
        return request.DeviceId.HasValue
            ? Ok(response)
            : CreatedAtAction(nameof(GetDevice), new { deviceId = registration.DeviceId }, response);
    }

    [HttpGet("{deviceId:guid}")]
    [ProducesResponseType(typeof(DeviceRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DeviceRegistrationResponse>> GetDevice(
        Guid deviceId,
        [FromServices] IDeviceAuthorizationService deviceAuthorizationService,
        CancellationToken cancellationToken)
    {
        await deviceAuthorizationService.AuthorizeDeviceAsync(User, deviceId, cancellationToken);
        var registration = await deviceRegistryService.GetDeviceAsync(deviceId, cancellationToken);
        return Ok(ToResponse(registration));
    }

    private static DeviceRegistrationResponse ToResponse(DeviceRegistration registration) => new()
    {
        DeviceId = registration.DeviceId,
        TenantId = registration.TenantId,
        UserId = registration.UserId,
        DisplayName = registration.DisplayName,
        Status = registration.Status,
        CreatedAt = registration.CreatedAt,
        LastSeenAt = registration.LastSeenAt
    };
}