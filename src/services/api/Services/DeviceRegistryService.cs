using System.Security.Claims;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupApi.Exceptions;
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

public sealed class DeviceRegistryService(
    [FromKeyedServices(StateStores.DeviceRegistry)] IStateDocumentStore store) : IDeviceRegistryService
{
    private const string DeviceDocument = "device";

    public async Task<DeviceRegistration> RegisterDeviceAsync(
        ClaimsPrincipal principal,
        Guid? requestedDeviceId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var (tenantId, userId) = DeviceIdentity.GetRequiredUserIdentity(principal);
        var deviceId = requestedDeviceId.GetValueOrDefault(Guid.NewGuid());

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("DeviceId cannot be empty", nameof(requestedDeviceId));
        }

        var document = await store.GetAsync<DeviceRegistration>(
            DeviceDocument, DevicePartition(deviceId), $"{deviceId:N}", cancellationToken);

        if (document != null)
        {
            var existing = document.Data;
            existing.ETag = document.ETag;

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

            refreshed.ETag = await store.UpsertAsync(
                DeviceDocument, DevicePartition(deviceId), $"{deviceId:N}", refreshed,
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

        registration.ETag = await store.UpsertAsync(
            DeviceDocument, DevicePartition(deviceId), $"{deviceId:N}", registration,
            cancellationToken: cancellationToken);

        return registration;
    }

    public async Task<DeviceRegistration> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var document = await store.GetAsync<DeviceRegistration>(
            DeviceDocument, DevicePartition(deviceId), $"{deviceId:N}", cancellationToken);

        if (document == null)
        {
            throw new DeviceNotRegisteredException(deviceId);
        }

        var registration = document.Data;
        registration.ETag = document.ETag;
        return registration;
    }

    private static bool IsOwner(DeviceRegistration registration, string tenantId, string userId)
        => string.Equals(registration.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(registration.UserId, userId, StringComparison.OrdinalIgnoreCase);

    private static string DevicePartition(Guid deviceId) => $"device:{deviceId:N}";
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
        var (tenantId, userId) = DeviceIdentity.GetRequiredUserIdentity(principal);

        if (registration.Status != DeviceRegistrationStatus.Active
            || !string.Equals(registration.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(registration.UserId, userId, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceAccessDeniedException(deviceId);
        }
    }

}

file static class DeviceIdentity
{
    public static (string TenantId, string UserId) GetRequiredUserIdentity(ClaimsPrincipal principal)
    {
        if (!principal.TryGetTenantId(out var tenantId) || !principal.TryGetUserId(out var userId))
        {
            throw new UserIdentityRequiredException();
        }

        return (tenantId, userId);
    }
}
