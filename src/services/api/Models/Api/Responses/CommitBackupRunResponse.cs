using FlorisDeV.BackupApi.Models.State;

namespace FlorisDeV.BackupApi.Models.Api.Responses;

/// <summary>
/// Response returned when a backup run commit is accepted for async processing.
/// </summary>
public class CommitBackupRunResponse
{
    /// <summary>
    /// The unique identifier for the commit job.
    /// Use this to poll for commit status.
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
    /// Current status of the commit job (typically Queued at response time).
    /// </summary>
    public CommitJobStatus Status { get; init; }
    
    /// <summary>
    /// When the commit job was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}
