using System.Runtime.CompilerServices;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Handles file scanning and smart hashing for backup operations.
/// Optimizes I/O by only computing hashes when necessary.
/// </summary>
public partial class BackupScanner(
    IFileSystemService fileSystemService,
    IBackupStateService backupStateService,
    ILogger<BackupScanner> logger)
    : IBackupScanner
{
    /// <summary>
    /// Scans all configured backup targets and yields files with their target metadata.
    /// </summary>
    public async IAsyncEnumerable<TaggedFile> ScanAllTargetsAsync(
        IReadOnlyDictionary<string, string> targets,
        string[]? excludePatterns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var (targetName, targetDirectory) in targets)
        {
            LogScanningTarget(targetName, targetDirectory);

            var targetIgnorePath = Path.Combine(targetDirectory, ".backupignore");
            var targetExcludePatterns = File.Exists(targetIgnorePath)
                ? BackupIgnoreParser.GetCombinedPatterns(targetIgnorePath)
                : excludePatterns;

            await foreach (var file in fileSystemService.ScanDirectoryStreamAsync(
                targetDirectory,
                targetExcludePatterns,
                cancellationToken))
            {
                yield return new TaggedFile(targetName, targetDirectory, file);
            }
        }
    }

    /// <summary>
    /// Performs smart hashing for a tagged file - only computes hash if needed.
    /// Returns the file with hash populated and a flag indicating if it needs backup.
    /// </summary>
    public async Task<(TaggedFile File, bool NeedsBackup, FileChangeType ChangeType)> AnalyzeFileAsync(
        TaggedFile taggedFile,
        CancellationToken cancellationToken)
    {
        var storagePath = taggedFile.GetStoragePath();
        var previousState = await backupStateService.GetFileStateAsync(storagePath, cancellationToken);

        if (previousState == null)
        {
            // New file - needs hash and backup
            try
            {
                var hash = await fileSystemService.ComputeFileHashAsync(taggedFile.Metadata.FilePath, cancellationToken);
                var fileWithHash = taggedFile with { Metadata = taggedFile.Metadata with { Hash = hash } };
                return (fileWithHash, true, FileChangeType.New);
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
            {
                LogFileSkipped(taggedFile.Metadata.FilePath, ex.Message);
                return (taggedFile, false, FileChangeType.Skipped);
            }
        }

        if (previousState.SizeBytes != taggedFile.Metadata.SizeBytes ||
            previousState.LastModifiedUtc != taggedFile.Metadata.LastModified)
        {
            // Potentially modified - compute hash to verify
            try
            {
                var hash = await fileSystemService.ComputeFileHashAsync(taggedFile.Metadata.FilePath, cancellationToken);
                var fileWithHash = taggedFile with { Metadata = taggedFile.Metadata with { Hash = hash } };

                return hash != previousState.Sha256Hash ?
                    // Content actually changed
                    (fileWithHash, true, FileChangeType.Modified) :
                    // Size/timestamp changed but content didn't (rare edge case - touch, copy with different timestamp)
                    (fileWithHash, false, FileChangeType.Unchanged);
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
            {
                LogFileSkipped(taggedFile.Metadata.FilePath, ex.Message);
                return (taggedFile, false, FileChangeType.Skipped);
            }
        }

        // Unchanged - reuse existing hash
        var fileWithCachedHash = taggedFile with { Metadata = taggedFile.Metadata with { Hash = previousState.Sha256Hash } };
        return (fileWithCachedHash, false, FileChangeType.Unchanged);
    }

    /// <summary>
    /// Detects files that were deleted since the last backup.
    /// </summary>
    public async Task<IReadOnlyList<string>> DetectDeletedFilesAsync(
        HashSet<string> scannedPaths,
        CancellationToken cancellationToken)
    {
        var deletedFiles = new List<string>();
        var previousFiles = await backupStateService.GetAllFileStatesAsync(cancellationToken);

        foreach (var previousFile in previousFiles)
        {
            if (!scannedPaths.Contains(previousFile.RelativePath))
            {
                deletedFiles.Add(previousFile.RelativePath);
            }
        }

        if (deletedFiles.Count > 0)
        {
            LogDeletedFilesDetected(deletedFiles.Count);
        }

        return deletedFiles;
    }

    #region Logging

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Scanning target '{TargetName}': {Directory}")]
    private partial void LogScanningTarget(string targetName, string directory);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Detected {Count} deleted files since last backup")]
    private partial void LogDeletedFilesDetected(int count);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Skipping inaccessible file '{FilePath}': {Reason}")]
    private partial void LogFileSkipped(string filePath, string reason);

    #endregion
}

/// <summary>
/// Type of change detected for a file.
/// </summary>
public enum FileChangeType
{
    New,
    Modified,
    Unchanged,
    Skipped
}
