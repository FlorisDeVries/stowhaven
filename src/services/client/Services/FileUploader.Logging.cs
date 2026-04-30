using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Logging methods for FileUploader.
/// </summary>
public partial class FileUploader
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Uploading {FileCount} files to staging area")]
    private partial void LogUploadingFiles(int fileCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Upload progress: {UploadedCount}/{TotalCount} files")]
    private partial void LogUploadProgress(int uploadedCount, int totalCount);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Upload complete: {FileCount} files")]
    private partial void LogUploadComplete(int fileCount);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Failed to upload file: {FilePath}")]
    private partial void LogFileUploadFailed(string filePath, Exception ex);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message = "Large file upload progress: {FileName} - {BytesTransferred}/{TotalBytes} bytes ({Percentage}%)")]
    private partial void LogLargeFileProgress(string fileName, long bytesTransferred, long totalBytes, int percentage);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Upload summary: {SuccessCount} succeeded, {FailedCount} failed out of {TotalCount} total")]
    private partial void LogUploadSummary(int successCount, int failedCount, int totalCount);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Large file may exceed timeout: {FilePath} ({SizeBytes} bytes, estimated {EstimatedSeconds}s upload, timeout {TimeoutSeconds}s). Consider increasing BlobUploadTimeoutSeconds.")]
    private partial void LogLargeFileTimeoutWarning(string filePath, long sizeBytes, long estimatedSeconds, int timeoutSeconds);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Information,
        Message = "Blob already exists for {FilePath} at {BlobPath}; treating upload as resumed.")]
    private partial void LogBlobAlreadyExists(string filePath, string blobPath);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Information,
        Message = "Run manifest already exists at {BlobPath}; treating manifest upload as resumed.")]
    private partial void LogRunManifestAlreadyExists(string blobPath);
}
