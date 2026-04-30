using FlorisDeV.BackupContracts.Infrastructure;

namespace FlorisDeV.BackupClient.Models;

/// <summary>
/// Durable local journal for an in-flight backup run. It lets the client resume after
/// interruption without creating a new server run or re-uploading completed blobs.
/// </summary>
public sealed record PendingBackupRun
{
    public required Guid DeviceId { get; init; }
    public required Guid RunId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required SasUrlInfo UploadSasUrlInfo { get; init; }
    public required SasUrlInfo ManifestSasUrlInfo { get; init; }
    public List<TaggedFile> UploadedChangedFiles { get; init; } = [];
    public List<string> DeletedFiles { get; init; } = [];
    public bool ManifestUploaded { get; init; }
    public Guid? CommitId { get; init; }

    public DateTimeOffset ExpiresAt => UploadSasUrlInfo.ExpiresAt < ManifestSasUrlInfo.ExpiresAt
        ? UploadSasUrlInfo.ExpiresAt
        : ManifestSasUrlInfo.ExpiresAt;

    public bool HasUsableSas(DateTimeOffset now, TimeSpan safetyWindow)
        => ExpiresAt > now.Add(safetyWindow);
}
