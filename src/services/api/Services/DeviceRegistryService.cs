using System.Security.Claims;
using Dapr.Client;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupContracts.Constants;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Security.Authentication;

namespace FlorisDeV.BackupApi.Services;

public interface IDeviceRegistryService
{
    Task<DeviceRegistration> RegisterDeviceAsync(
        ClaimsPrincipal principal,
        Guid? requestedDeviceId,
        string? displayName,
        CancellationToken cancellationToken = default);

    Task<DeviceRegistration> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

public sealed class DeviceRegistryService(DaprClient daprClient) : IDeviceRegistryService
{
    public async Task<DeviceRegistration> RegisterDeviceAsync(
        ClaimsPrincipal principal,
        Guid? requestedDeviceId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var tenantId = principal.GetTenantId();
        var userId = principal.GetUserId();
        var deviceId = requestedDeviceId.GetValueOrDefault(Guid.NewGuid());

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("DeviceId cannot be empty", nameof(requestedDeviceId));
        }

        var stateKey = GetDeviceStateKey(deviceId);
        var (existing, etag) = await daprClient.GetStateAndETagAsync<DeviceRegistration>(
            DaprComponents.DeviceRegistryStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (existing != null)
        {
            existing.ETag = etag;

            if (!IsOwner(existing, tenantId, userId))
            {
                throw new DeviceAlreadyRegisteredException(deviceId);
            }

            if (existing.Status != DeviceRegistrationStatus.Active)
            {
                throw new DeviceAccessDeniedException(deviceId);
            }

            var refreshed = existing with
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? existing.DisplayName : displayName,
                LastSeenAt = DateTimeOffset.UtcNow
            };

            await daprClient.SaveStateAsync(
                DaprComponents.DeviceRegistryStateStore,
                stateKey,
                refreshed,
                cancellationToken: cancellationToken);

            return refreshed;
        }

        var now = DateTimeOffset.UtcNow;
        var registration = new DeviceRegistration
        {
            DeviceId = deviceId,
            TenantId = tenantId,
            UserId = userId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            Status = DeviceRegistrationStatus.Active,
            CreatedAt = now,
            LastSeenAt = now
        };

        await daprClient.SaveStateAsync(
            DaprComponents.DeviceRegistryStateStore,
            stateKey,
            registration,
            cancellationToken: cancellationToken);

        return registration;
    }

    public async Task<DeviceRegistration> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var stateKey = GetDeviceStateKey(deviceId);
        var (registration, etag) = await daprClient.GetStateAndETagAsync<DeviceRegistration>(
            DaprComponents.DeviceRegistryStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (registration == null)
        {
            throw new DeviceNotRegisteredException(deviceId);
        }

        registration.ETag = etag;
        return registration;
    }

    private static bool IsOwner(DeviceRegistration registration, string tenantId, string userId)
        => string.Equals(registration.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(registration.UserId, userId, StringComparison.OrdinalIgnoreCase);

    private static string GetDeviceStateKey(Guid deviceId) => $"devices/{deviceId:N}";
}

public interface IDeviceAuthorizationService
{
    Task AuthorizeDeviceAsync(
        ClaimsPrincipal principal,
        Guid deviceId,
        CancellationToken cancellationToken = default);
}

public sealed class DeviceAuthorizationService(IDeviceRegistryService deviceRegistry) : IDeviceAuthorizationService
{
    public async Task AuthorizeDeviceAsync(
        ClaimsPrincipal principal,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var registration = await deviceRegistry.GetDeviceAsync(deviceId, cancellationToken);
        var tenantId = principal.GetTenantId();
        var userId = principal.GetUserId();

        if (registration.Status != DeviceRegistrationStatus.Active
            || !string.Equals(registration.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(registration.UserId, userId, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceAccessDeniedException(deviceId);
        }
    }
}
