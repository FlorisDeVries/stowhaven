namespace FlorisDeV.BackupClient.Models;

// Portable evidence only. This contract deliberately has no state, credentials, paths or exception text.
public sealed record BackupRunResult
{
    public Guid RunId { get; init; }
    public Guid? CommitId { get; init; }
    public Guid? OriginAttemptId { get; init; }
    public Guid[] AttemptIds { get; init; } = [];
    public DateTimeOffset? ScanStartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? ServerCompletedAt { get; init; }
    public DateTimeOffset? ServerCompletionObservedAt { get; init; }
    public string? SelectionFingerprint { get; init; }
    public int? FilesSkipped { get; init; }
    public int? FilesDeferred { get; init; }
    public int? FilesFailed { get; init; }
    public int? ServerFilesFailed { get; init; }
    public bool? ScanCompleted { get; init; }
    public string Outcome { get; init; } = "pending";
    public string ServerProcessing { get; init; } = "unknown";
    public string LocalCompletion { get; init; } = "pending";
    public string Cleanliness { get; init; } = "unknown";
}

public sealed record BackupScanEvidence(DateTimeOffset StartedAt, string SelectionFingerprint,
    bool Completed, bool? Clean, bool NoChanges);
