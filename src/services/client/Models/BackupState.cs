using FlorisDeV.BackupClient.Services;

namespace FlorisDeV.BackupClient.Models;

/// <summary>
/// Stores metadata about the device and last successful backup run.
/// Single record per device.
/// </summary>
public record DeviceState(
    Guid DeviceId,
    DateTimeOffset? LastSuccessfulBackup,
    Guid? LastRunId,
    string? LastCommitId,
    long TotalFilesTracked,
    long TotalBytesTracked);

/// <summary>
/// Tracks the state of each file from the last successful backup.
/// Used for delta detection (comparing current filesystem vs. last backup).
/// </summary>
public record BackupFileState(
    string RelativePath,
    string Sha256Hash,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    DateTimeOffset BackedUpAt,
    Guid BackupRunId,
    string? UniqueFileId);

/// <summary>
/// Result of comparing current filesystem against last backup state.
/// </summary>
public record BackupDelta(
    IReadOnlyList<FileMetadata> NewFiles,
    IReadOnlyList<FileMetadata> ModifiedFiles,
    IReadOnlyList<string> DeletedFiles,
    long TotalBytes);
