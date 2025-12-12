using FlorisDeV.BackupApi.Models.Infrastructure;
using FlorisDeV.BackupApi.Models.State;

namespace FlorisDeV.BackupApi.Models.Api.Responses;

public class StartBackupRunResponse
{
    public Guid DeviceId { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public BackupRunStatus Status { get; set; }
    public SasUrlInfo SasUrlInfo { get; set; }
}