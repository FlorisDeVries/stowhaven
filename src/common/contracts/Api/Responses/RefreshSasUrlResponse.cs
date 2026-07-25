using FlorisDeV.BackupContracts.Infrastructure;

namespace FlorisDeV.BackupContracts.Api.Responses;

/// <summary>
/// Re-issued SAS URLs for an in-progress backup run whose original tokens are close to expiry.
/// The run is not restarted: the same staging/manifest directories are re-signed with a fresh window.
/// </summary>
public class RefreshSasUrlResponse
{
    public Guid DeviceId { get; init; }
    public Guid RunId { get; init; }
    public SasUrlInfo SasUrlInfo { get; init; } = null!;
    public SasUrlInfo ManifestSasUrlInfo { get; init; } = null!;
}
