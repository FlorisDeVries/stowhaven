using FlorisDeV.BackupContracts.Infrastructure;

namespace FlorisDeV.BackupContracts.Application;

/// <summary>
/// Result of re-issuing SAS URLs for an existing backup run (no new run is created).
/// </summary>
public class BackupRunSasRefreshResult
{
    public Guid DeviceId { get; init; }
    public Guid RunId { get; init; }
    public SasUrlInfo SasUrl { get; init; } = null!;
    public SasUrlInfo ManifestSasUrl { get; init; } = null!;
}
