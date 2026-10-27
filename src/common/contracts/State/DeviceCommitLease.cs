namespace FlorisDeV.BackupContracts.State;

/// <summary>Serializes catalog mutations for a device and remembers the newest completed run.</summary>
public sealed class DeviceCommitLease
{
    public Guid DeviceId { get; set; }
    public Guid CommitId { get; set; }
    public Guid? ClaimId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LatestRunStartedAt { get; set; }
    public Guid? LatestRunId { get; set; }
}
