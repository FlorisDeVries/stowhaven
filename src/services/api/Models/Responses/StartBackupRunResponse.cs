using FlorisDeV.BackupApi.Models.StateStore;

namespace FlorisDeV.BackupApi.Models.Responses;

public class StartBackupRunResponse
{
    public Guid DeviceId { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public BackupRunStatus Status { get; set; }
}