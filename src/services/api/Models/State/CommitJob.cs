namespace FlorisDeV.BackupApi.Models.State;

/// <summary>
/// Represents an asynchronous commit job that processes a backup run.
/// </summary>
public class CommitJob
{
    /// <summary>
    /// Unique identifier for this commit job.
    /// </summary>
    public Guid CommitId { get; set; }
    
    /// <summary>
    /// The device ID associated with this commit.
    /// </summary>
    public Guid DeviceId { get; set; }
    
    /// <summary>
    /// The backup run ID being committed.
    /// </summary>
    public Guid RunId { get; set; }
    
    /// <summary>
    /// Current status of the commit job.
    /// </summary>
    public CommitJobStatus Status { get; set; }
    
    /// <summary>
    /// Error message if the commit failed.
    /// </summary>
    public string? Error { get; set; }
    
    /// <summary>
    /// When the commit job was created (queued).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
    
    /// <summary>
    /// When the commit job was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
    
    /// <summary>
    /// When the commit job was completed (succeeded or failed).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }
    
    /// <summary>
    /// Number of files processed in this commit.
    /// </summary>
    public int FilesProcessed { get; set; }
    
    /// <summary>
    /// ETag for optimistic concurrency control.
    /// </summary>
    public string? ETag { get; set; }
}

public enum CommitJobStatus
{
    /// <summary>
    /// Commit job has been created and is waiting to be processed.
    /// </summary>
    Queued,
    
    /// <summary>
    /// Commit job is currently being processed by a worker.
    /// </summary>
    Processing,
    
    /// <summary>
    /// Commit job completed successfully.
    /// </summary>
    Succeeded,
    
    /// <summary>
    /// Commit job failed due to an error.
    /// </summary>
    Failed
}
