namespace FlorisDeV.BackupContracts.Events;

/// <summary>
/// Event published when a backup run is committed and ready for processing.
/// </summary>
public class BackupRunCommittedEvent
{
    public Guid CommitId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid RunId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CommittedAt { get; init; }
    public string StagingPath { get; init; } = null!;
    public string? ManifestPath { get; init; }
}