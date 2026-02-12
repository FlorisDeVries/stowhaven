using FlorisDeV.BackupApi.Models.Infrastructure;
using FlorisDeV.BackupApi.Models.State;

namespace FlorisDeV.BackupApi.Models.Api.Responses;

public class StartBackupRunResponse
{
    public Guid DeviceId { get; init; }
    public Guid RunId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public BackupRunStatus Status { get; init; }
    public SasUrlInfo SasUrlInfo { get; init; } = null!;
}