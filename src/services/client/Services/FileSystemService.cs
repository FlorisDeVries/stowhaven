using System.Security.Cryptography;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Provides file system operations for backup operations, including directory scanning,
/// file reading, and hash computation.
/// </summary>
public interface IFileSystemService
{
    /// <summary>
    /// Recursively scans a directory and returns metadata for all files found.
    /// Files and directories matching exclude patterns are skipped.
    /// </summary>
    /// <param name="directoryPath">
    /// The absolute path to the root directory to scan. Must exist.
    /// </param>
    /// <param name="excludePatterns">
    /// Optional glob patterns to exclude files and directories (e.g., "**/node_modules/**", "*.tmp", ".git/**").
    /// Uses Microsoft.Extensions.FileSystemGlobbing syntax. If null, no exclusions are applied.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>
    /// A read-only list of file metadata for all files found. Files that cannot be accessed
    /// due to permission issues are logged and skipped (not included in results).
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified directory does not exist.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    Task<IReadOnlyList<FileMetadata>> ScanDirectoryAsync(
        string directoryPath,
        string[]? excludePatterns = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a file and returns a readable stream for its contents.
    /// The caller is responsible for disposing the returned stream.
    /// </summary>
    /// <param name="filePath">The absolute path to the file to read. Must exist.</param>
    /// <param name="cancellationToken">Cancellation token (note: opening the stream is synchronous).</param>
    /// <returns>
    /// A <see cref="FileStream"/> opened with read access and shared read permissions.
    /// The stream uses an 80KB buffer and async I/O for optimal performance.
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown when the specified file does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when access to the file is denied.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs opening the file.</exception>
    /// <remarks>
    /// Always dispose the returned stream when done, preferably using 'await using' or 'using' statements.
    /// </remarks>
    Task<Stream> GetFileStreamAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the SHA256 hash of a file's contents.
    /// </summary>
    /// <param name="filePath">The absolute path to the file to hash. Must exist.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>
    /// The SHA256 hash as a lowercase hexadecimal string (64 characters, e.g., "a1b2c3...").
    /// Suitable for use as a unique file identifier or for content-based deduplication.
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown when the specified file does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when access to the file is denied.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs reading the file.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    /// <remarks>
    /// Uses async streaming for efficient memory usage with large files.
    /// </remarks>
    Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves metadata for a single file without computing its hash.
    /// </summary>
    /// <param name="filePath">The absolute path to the file. Must exist.</param>
    /// <param name="cancellationToken">Cancellation token (note: metadata retrieval is synchronous).</param>
    /// <returns>
    /// A <see cref="FileMetadata"/> record containing the file's path, size, and timestamps.
    /// The <see cref="FileMetadata.Hash"/> property will be null.
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown when the specified file does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when access to the file is denied.</exception>
    /// <remarks>
    /// Use this for quick metadata retrieval. For hash computation, call <see cref="ComputeFileHashAsync"/> separately.
    /// All timestamps are returned in UTC.
    /// </remarks>
    Task<FileMetadata> GetFileMetadataAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents metadata for a file in the file system.
/// </summary>
/// <param name="FilePath">The absolute path to the file.</param>
/// <param name="SizeBytes">The size of the file in bytes.</param>
/// <param name="LastModified">The UTC timestamp when the file was last modified.</param>
/// <param name="Created">The UTC timestamp when the file was created.</param>
/// <param name="Hash">
/// Optional SHA256 hash of the file contents as a lowercase hexadecimal string.
/// Null if the hash has not been computed yet.
/// </param>
public record FileMetadata(
    string FilePath,
    long SizeBytes,
    DateTimeOffset LastModified,
    DateTimeOffset Created,
    string? Hash = null);

public partial class FileSystemService(ILogger<FileSystemService> logger) : IFileSystemService
{
    public async Task<IReadOnlyList<FileMetadata>> ScanDirectoryAsync(
        string directoryPath,
        string[]? excludePatterns = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        var files = new List<FileMetadata>();

        // Setup glob matcher for exclusions
        Matcher? exclusionMatcher = null;
        if (excludePatterns?.Length > 0)
        {
            exclusionMatcher = new Matcher();
            exclusionMatcher.AddInclude("**/*"); // Include all files by default
            foreach (var pattern in excludePatterns)
            {
                // Convert simple extension patterns (*.ext) to recursive patterns (**/*.ext)
                // to match user expectations of excluding files at any level
                var normalizedPattern = pattern.StartsWith("*.") && !pattern.Contains('/') && !pattern.Contains('\\')
                    ? $"**/{pattern}"
                    : pattern;
                exclusionMatcher.AddExclude(normalizedPattern);
            }
        }

        await ScanDirectoryRecursiveAsync(directoryPath, directoryPath, files, exclusionMatcher, cancellationToken);

        LogDirectoryScanned(directoryPath, files.Count);
        return files;
    }

    private async Task ScanDirectoryRecursiveAsync(
        string rootPath,
        string currentPath,
        List<FileMetadata> files,
        Matcher? exclusionMatcher,
        CancellationToken cancellationToken)
    {
        try
        {
            // Scan files in current directory
            foreach (var filePath in Directory.EnumerateFiles(currentPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(rootPath, filePath);

                // Check if file should be excluded
                if (exclusionMatcher != null)
                {
                    var matchResult = exclusionMatcher.Match(relativePath);
                    if (!matchResult.HasMatches)
                    {
                        continue;
                    }
                }

                try
                {
                    var metadata = await GetFileMetadataAsync(filePath, cancellationToken);
                    files.Add(metadata);
                }
                catch (Exception ex)
                {
                    LogFileMetadataError(filePath, ex);
                }
            }

            // Recursively scan subdirectories
            foreach (var directory in Directory.EnumerateDirectories(currentPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dirInfo = new DirectoryInfo(directory);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Skip symbolic links to avoid loops
                    continue;
                }

                var relativePath = Path.GetRelativePath(rootPath, directory);

                // Check if directory should be excluded
                if (exclusionMatcher != null)
                {
                    var matchResult = exclusionMatcher.Match(relativePath);
                    if (!matchResult.HasMatches)
                    {
                        continue;
                    }
                }

                await ScanDirectoryRecursiveAsync(rootPath, directory, files, exclusionMatcher, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            LogDirectoryAccessDenied(currentPath, ex);
        }
    }

    public Task<Stream> GetFileStreamAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        try
        {
            var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920, // 80KB buffer for better performance
                useAsync: true);

            return Task.FromResult<Stream>(stream);
        }
        catch (Exception ex)
        {
            LogFileStreamError(filePath, ex);
            throw;
        }
    }

    public async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            LogHashComputationError(filePath, ex);
            throw;
        }
    }

    public Task<FileMetadata> GetFileMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        try
        {
            var fileInfo = new FileInfo(filePath);

            var metadata = new FileMetadata(
                FilePath: filePath,
                SizeBytes: fileInfo.Length,
                LastModified: fileInfo.LastWriteTimeUtc,
                Created: fileInfo.CreationTimeUtc);

            return Task.FromResult(metadata);
        }
        catch (Exception ex)
        {
            LogFileMetadataError(filePath, ex);
            throw;
        }
    }

    #region Logging

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Scanned directory {DirectoryPath}, found {FileCount} files")]
    private partial void LogDirectoryScanned(string directoryPath, int fileCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Failed to get metadata for file: {FilePath}")]
    private partial void LogFileMetadataError(string filePath, Exception ex);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Access denied to directory: {DirectoryPath}")]
    private partial void LogDirectoryAccessDenied(string directoryPath, Exception ex);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Failed to open file stream: {FilePath}")]
    private partial void LogFileStreamError(string filePath, Exception ex);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Error,
        Message = "Failed to compute hash for file: {FilePath}")]
    private partial void LogHashComputationError(string filePath, Exception ex);

    #endregion
}