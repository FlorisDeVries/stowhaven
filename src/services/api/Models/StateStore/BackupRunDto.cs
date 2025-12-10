namespace FlorisDeV.BackupApi.Models.StateStore;

public class BackupRunDto
{
    public Guid DeviceId { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public BackupRunStatus Status { get; set; }
    public int FilesBackedUp { get; set; }
}

public class RunStatistics
{
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
}

public enum BackupRunStatus
{
    Queued,
    Processing,
    Succeeded,
    Failed
}