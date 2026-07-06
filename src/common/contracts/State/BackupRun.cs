namespace FlorisDeV.BackupContracts.State;

public class BackupRun
{
    public Guid DeviceId { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public BackupRunStatus Status { get; set; }
    public int FilesBackedUp { get; set; }
    public string? ETag { get; set; }
}

public class RunStatistics
{
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
}

public sealed record BackupRunQuery
{
    public Guid? DeviceId { get; init; }
    public DateTimeOffset? StartedFromUtc { get; init; }
    public DateTimeOffset? StartedToUtc { get; init; }
    public BackupRunStatus? Status { get; init; }
    public int PageSize { get; init; } = 100;
    public string? ContinuationToken { get; init; }
}

public sealed record BackupRunPage
{
    public required IReadOnlyList<BackupRun> Runs { get; init; }
    public required int PageSize { get; init; }
    public string? ContinuationToken { get; init; }
    public string? NextContinuationToken { get; init; }
    public bool HasMore => !string.IsNullOrWhiteSpace(NextContinuationToken);
}

public enum BackupRunStatus
{
    Queued,
    Processing,
    Succeeded,
    Failed
}