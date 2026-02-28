using FlorisDeV.BackupClient.Models;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Computes backup deltas by comparing current files against previously backed up files.
/// Identifies new, modified, and deleted files for efficient incremental backups.
/// </summary>
public partial class BackupDeltaComputer(
    IBackupStateService stateService,
    ILogger<BackupDeltaComputer> logger)
{
    /// <summary>
    /// Computes the backup delta by comparing current files with previously backed up state.
    /// </summary>
    /// <param name="currentFiles">Current files from filesystem scan</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Delta containing new, modified, and deleted files</returns>
    public async Task<BackupDelta> ComputeDeltaAsync(
        IReadOnlyList<FileMetadata> currentFiles,
        CancellationToken cancellationToken = default)
    {
        LogComputingDelta(currentFiles.Count);

        var previousFiles = await LoadPreviousFileStatesAsync(cancellationToken);

        var (newFiles, modifiedFiles, totalBytes) = IdentifyNewAndModifiedFiles(
            currentFiles,
            previousFiles);

        var deletedFiles = IdentifyDeletedFiles(currentFiles, previousFiles);

        var delta = new BackupDelta(newFiles, modifiedFiles, deletedFiles, totalBytes);

        LogDeltaComputed(newFiles.Count, modifiedFiles.Count, deletedFiles.Count, totalBytes);
        return delta;
    }

    private async Task<Dictionary<string, BackupFileState>> LoadPreviousFileStatesAsync(
        CancellationToken cancellationToken)
    {
        var allStates = await stateService.GetAllFileStatesAsync(cancellationToken);
        var stateDict = new Dictionary<string, BackupFileState>(
            allStates.Count,
            StringComparer.OrdinalIgnoreCase);

        foreach (var state in allStates)
        {
            stateDict[state.RelativePath] = state;
        }

        return stateDict;
    }

    private (List<FileMetadata> NewFiles, List<FileMetadata> ModifiedFiles, long TotalBytes)
        IdentifyNewAndModifiedFiles(
            IReadOnlyList<FileMetadata> currentFiles,
            Dictionary<string, BackupFileState> previousFiles)
    {
        var newFiles = new List<FileMetadata>();
        var modifiedFiles = new List<FileMetadata>();
        long totalBytes = 0;

        foreach (var currentFile in currentFiles)
        {
            if (!previousFiles.TryGetValue(currentFile.FilePath, out var previousFile))
            {
                // New file - not in previous backup
                newFiles.Add(currentFile);
                totalBytes += currentFile.SizeBytes;
                continue;
            }

            if (IsFileModified(currentFile, previousFile))
            {
                modifiedFiles.Add(currentFile);
                totalBytes += currentFile.SizeBytes;
            }
        }

        return (newFiles, modifiedFiles, totalBytes);
    }

    private bool IsFileModified(FileMetadata current, BackupFileState previous)
    {
        // Size change is always a modification
        if (current.SizeBytes != previous.SizeBytes)
            return true;

        // Hash change indicates modification (if hash is computed)
        if (current.Hash != null && current.Hash != previous.Sha256Hash)
            return true;

        return false;
    }

    private List<string> IdentifyDeletedFiles(
        IReadOnlyList<FileMetadata> currentFiles,
        Dictionary<string, BackupFileState> previousFiles)
    {
        var currentPaths = new HashSet<string>(
            currentFiles.Select(f => f.FilePath),
            StringComparer.OrdinalIgnoreCase);

        return previousFiles.Keys
            .Where(path => !currentPaths.Contains(path))
            .ToList();
    }

    #region Logging

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Computing delta for {FileCount} current files")]
    private partial void LogComputingDelta(int fileCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Delta computed: New={NewCount}, Modified={ModifiedCount}, Deleted={DeletedCount}, TotalBytes={TotalBytes}")]
    private partial void LogDeltaComputed(int newCount, int modifiedCount, int deletedCount, long totalBytes);

    #endregion
}
