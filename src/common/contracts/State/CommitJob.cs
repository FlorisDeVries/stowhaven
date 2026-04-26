namespace FlorisDeV.BackupContracts.State;

/// <summary>
/// Represents an asynchronous commit job that processes a backup run.
/// </summary>
public class CommitJob
{
    public Guid CommitId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid RunId { get; set; }
    public CommitJobStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int FilesProcessed { get; set; }
    public string? ETag { get; set; }
}

public enum CommitJobStatus
{
    Queued,
    Processing,
    Succeeded,
    Failed
}