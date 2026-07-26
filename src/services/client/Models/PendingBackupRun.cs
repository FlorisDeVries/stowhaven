using System.Text.Json.Serialization;
using FlorisDeV.BackupContracts.Infrastructure;

namespace FlorisDeV.BackupClient.Models;

/// <summary>
/// Durable local journal header for an in-flight backup run. It lets the client resume after
/// interruption without creating a new server run or re-uploading completed blobs.
///
/// This record holds run-level metadata only. The uploaded files and detected deletions are kept in
/// separate append-only tables and are read back as streams, so a run covering hundreds of
/// thousands of files never has to be materialized in memory. <see cref="UploadedFileCount"/> and
/// <see cref="DeletedFileCount"/> are derived from those tables when the header is loaded.
/// </summary>
public sealed record PendingBackupRun
{
    public required Guid DeviceId { get; init; }
    public required Guid RunId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required SasUrlInfo UploadSasUrlInfo { get; init; }
    public required SasUrlInfo ManifestSasUrlInfo { get; init; }
    public bool ManifestUploaded { get; init; }
    public Guid? CommitId { get; init; }

    /// <summary>Files already staged for this run. Derived on load; never persisted in the header.</summary>
    [JsonIgnore]
    public int UploadedFileCount { get; init; }

    /// <summary>Deletions recorded for this run. Derived on load; never persisted in the header.</summary>
    [JsonIgnore]
    public int DeletedFileCount { get; init; }

    public DateTimeOffset ExpiresAt => UploadSasUrlInfo.ExpiresAt < ManifestSasUrlInfo.ExpiresAt
        ? UploadSasUrlInfo.ExpiresAt
        : ManifestSasUrlInfo.ExpiresAt;

    public bool HasUsableSas(DateTimeOffset now, TimeSpan safetyWindow)
        => ExpiresAt > now.Add(safetyWindow);
}
