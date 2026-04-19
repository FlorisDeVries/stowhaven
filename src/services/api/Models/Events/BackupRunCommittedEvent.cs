namespace FlorisDeV.BackupApi.Models.Events;

/// <summary>
/// Event published when a backup run is committed and ready for processing.
/// </summary>
public class BackupRunCommittedEvent
{
    /// <summary>
    /// The unique identifier for the commit job.
    /// </summary>
    public Guid CommitId { get; init; }
    
    /// <summary>
    /// The device ID associated with this backup run.
    /// </summary>
    public Guid DeviceId { get; init; }
    
    /// <summary>
    /// The unique identifier for this backup run.
    /// </summary>
    public Guid RunId { get; init; }
    
    /// <summary>
    /// When the backup run was started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }
    
    /// <summary>
    /// When the commit job was created.
    /// </summary>
    public DateTimeOffset CommittedAt { get; init; }
    
    /// <summary>
    /// The staging path where files were uploaded.
    /// Format: staging/{deviceId}/{runId}/
    /// </summary>
    public string StagingPath { get; init; } = null!;
    
    /// <summary>
    /// The path to the run-manifest.json blob.
    /// Format: runs/{deviceId}/{runId}/run-manifest.json
    /// </summary>
    public string? ManifestPath { get; init; }
}
