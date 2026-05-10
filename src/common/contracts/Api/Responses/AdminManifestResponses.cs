using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupContracts.Api.Responses;

public sealed record ListManifestsResponse
{
    public required IReadOnlyList<ManifestSummaryResponse> Manifests { get; init; }
    public required int PageSize { get; init; }
    public string? ContinuationToken { get; init; }
    public string? NextContinuationToken { get; init; }
    public bool HasMore => !string.IsNullOrWhiteSpace(NextContinuationToken);
}

public sealed record ManifestSummaryResponse
{
    public required Guid DeviceId { get; init; }
    public required Guid RunId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required BackupRunStatus Status { get; init; }
    public required int FilesBackedUp { get; init; }
    public Guid? CommitId { get; init; }
    public CommitJobStatus? CommitStatus { get; init; }
}

public sealed record ManifestDetailsResponse
{
    public required ManifestSummaryResponse Summary { get; init; }
    public CommitStatusResponse? Commit { get; init; }
    public RunManifest? Manifest { get; init; }
    public required bool ManifestAvailable { get; init; }
    public string? ManifestUnavailableReason { get; init; }
}

public sealed record ManifestFilesResponse
{
    public required Guid DeviceId { get; init; }
    public required Guid RunId { get; init; }
    public required IReadOnlyList<ManifestFileEntry> Files { get; init; }
    public required IReadOnlyList<string> Deleted { get; init; }
    public required int FileCount { get; init; }
    public required int DeletedCount { get; init; }
}
