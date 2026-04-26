using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupContracts.Api.Responses;

public class CommitBackupRunResponse
{
    public Guid CommitId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid RunId { get; init; }
    public CommitJobStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}