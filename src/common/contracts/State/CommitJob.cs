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
    public int TotalFiles { get; set; }
    public int FilesFailed { get; set; }
    public int AttemptCount { get; set; }
    public string? FailureCategory { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public string? ETag { get; set; }
}

public enum CommitJobStatus
{
    Queued,
    Processing,
    Succeeded,
    CompletedWithErrors,
    Failed
}

public sealed record CommitJobQuery
{
    public Guid? DeviceId { get; init; }
    public Guid? RunId { get; init; }
    public CommitJobStatus? Status { get; init; }
    public DateTimeOffset? CreatedFromUtc { get; init; }
    public DateTimeOffset? CreatedToUtc { get; init; }
    public int PageSize { get; init; } = 100;
    public string? ContinuationToken { get; init; }
}

public sealed record CommitJobPage
{
    public required IReadOnlyList<CommitJob> Commits { get; init; }
    public required int PageSize { get; init; }
    public string? ContinuationToken { get; init; }
    public string? NextContinuationToken { get; init; }
    public bool HasMore => !string.IsNullOrWhiteSpace(NextContinuationToken);
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

public sealed record CommitFileProgressPage
{
    public required IReadOnlyList<CommitFileProgress> Files { get; init; }
    public required int PageSize { get; init; }
    public string? ContinuationToken { get; init; }
    public string? NextContinuationToken { get; init; }
    public bool HasMore => !string.IsNullOrWhiteSpace(NextContinuationToken);
}