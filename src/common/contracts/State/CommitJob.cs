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

/// <summary>
/// Tracks deterministic, retry-safe progress for a single file within a commit job.
/// </summary>
public class CommitFileProgress
{
    public Guid CommitId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid RunId { get; set; }
    public required string UniqueFileId { get; set; }
    public required string LogicalPath { get; set; }
    public CommitFileStatus Status { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Error { get; set; }
    public string? ETag { get; set; }
}

public enum CommitFileStatus
{
    Pending,
    Moved,
    StateUpdated,
    Succeeded,
    Failed
}