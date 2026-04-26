using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupContracts.Api.Responses;

public class StartBackupRunResponse
{
    public Guid DeviceId { get; init; }
    public Guid RunId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public BackupRunStatus Status { get; init; }
    public SasUrlInfo SasUrlInfo { get; init; } = null!;
    public SasUrlInfo ManifestSasUrlInfo { get; init; } = null!;
}