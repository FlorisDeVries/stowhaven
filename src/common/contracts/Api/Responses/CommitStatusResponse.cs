using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupContracts.Api.Responses;

public class CommitStatusResponse
{
    public Guid CommitId { get; init; }
    public Guid DeviceId { get; init; }
    public Guid RunId { get; init; }
    public CommitJobStatus Status { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int? FilesProcessed { get; init; }
    public int AttemptCount { get; init; }
    public string? FailureCategory { get; init; }
    public DateTimeOffset? LastErrorAt { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public DateTimeOffset? DeadLetteredAt { get; init; }
}