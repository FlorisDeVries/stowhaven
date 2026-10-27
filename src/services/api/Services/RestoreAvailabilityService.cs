using Azure.Storage.Blobs.Models;
using FlorisDeV.BackupContracts.Api.Responses;

namespace FlorisDeV.BackupApi.Services;

public interface IRestoreAvailabilityService
{
    Task<RestoreFileAvailability> EnsureAvailableAsync(Guid deviceId, string uniqueFileId, bool rehydrate, CancellationToken token);
}

public sealed class RestoreAvailabilityService(IBlobStorageService storage) : IRestoreAvailabilityService
{
    public async Task<RestoreFileAvailability> EnsureAvailableAsync(Guid deviceId, string uniqueFileId, bool rehydrate, CancellationToken token)
    {
        var container = await storage.GetContainerClientAsync(token);
        var blob = container.GetBlobClient($"devices/{deviceId:N}/files/{uniqueFileId}");
        var properties = (await blob.GetPropertiesAsync(cancellationToken: token)).Value;
        if (properties.AccessTier != AccessTier.Archive) return RestoreFileAvailability.Ready;
        if (properties.ArchiveStatus != null) return RestoreFileAvailability.Rehydrating;
        if (!rehydrate) return RestoreFileAvailability.Archived;
        await blob.SetAccessTierAsync(AccessTier.Hot, rehydratePriority: RehydratePriority.Standard, cancellationToken: token);
        return RestoreFileAvailability.Rehydrating;
    }
}
