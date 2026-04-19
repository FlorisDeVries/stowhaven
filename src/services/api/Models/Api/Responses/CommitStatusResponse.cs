using FlorisDeV.BackupApi.Models.State;

namespace FlorisDeV.BackupApi.Models.Api.Responses;

/// <summary>
/// Response containing the current status of a commit job.
/// </summary>
public class CommitStatusResponse
{
    /// <summary>
    /// The unique identifier for the commit job.
    /// </summary>
    public Guid CommitId { get; init; }
    
    /// <summary>
    /// The device ID associated with this commit.
    /// </summary>
    public Guid DeviceId { get; init; }
    
    /// <summary>
    /// The backup run ID being committed.
    /// </summary>
    public Guid RunId { get; init; }
    
    /// <summary>
    /// Current status of the commit job.
    /// </summary>
    public CommitJobStatus Status { get; init; }
    
    /// <summary>
    /// Error message if the commit failed.
    /// </summary>
    public string? Error { get; init; }
    
    /// <summary>
    /// When the commit job was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
    
    /// <summary>
    /// When the commit job was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }
    
    /// <summary>
    /// When the commit job completed (if succeeded or failed).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }
    
    /// <summary>
    /// Number of files processed (available when completed).
    /// </summary>
    public int? FilesProcessed { get; init; }
}
