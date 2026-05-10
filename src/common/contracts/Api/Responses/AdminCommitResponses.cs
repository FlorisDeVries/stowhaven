using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupContracts.Api.Responses;

public sealed record ListCommitJobsResponse
{
    public required IReadOnlyList<CommitStatusResponse> Commits { get; init; }
    public required int PageSize { get; init; }
    public string? ContinuationToken { get; init; }
    public string? NextContinuationToken { get; init; }
    public bool HasMore => !string.IsNullOrWhiteSpace(NextContinuationToken);
}

public sealed record CommitJobDetailsResponse
{
    public required CommitStatusResponse Commit { get; init; }
    public BackupRun? BackupRun { get; init; }
    public required CommitFileProgressCounts Progress { get; init; }
    public required bool ManifestAvailable { get; init; }
    public string? ManifestUnavailableReason { get; init; }
}

public sealed record CommitFileProgressCounts
{
    public required int Total { get; init; }
    public required int Pending { get; init; }
    public required int Moved { get; init; }
    public required int StateUpdated { get; init; }
    public required int Succeeded { get; init; }
    public required int Failed { get; init; }
}

public sealed record ListCommitFileProgressResponse
{
    public required Guid CommitId { get; init; }
    public required Guid DeviceId { get; init; }
    public required Guid RunId { get; init; }
    public required IReadOnlyList<CommitFileProgressResponse> Files { get; init; }
    public required int PageSize { get; init; }
    public string? ContinuationToken { get; init; }
    public string? NextContinuationToken { get; init; }
    public bool HasMore => !string.IsNullOrWhiteSpace(NextContinuationToken);
}

public sealed record CommitFileProgressResponse
{
    public required Guid CommitId { get; init; }
    public required Guid DeviceId { get; init; }
    public required Guid RunId { get; init; }
    public required string UniqueFileId { get; init; }
    public required string LogicalPath { get; init; }
    public required CommitFileStatus Status { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Error { get; init; }
}
