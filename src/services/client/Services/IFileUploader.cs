using Azure.Storage.Blobs;
using FlorisDeV.BackupClient.Models;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Handles parallel file uploads to Azure Blob Storage with retry logic and progress tracking.
/// </summary>
public interface IFileUploader
{
    /// <summary>
    /// Sets the base path prefix for uploaded blobs (e.g., "staging/device/run/").
    /// Must be called before uploading files.
    /// </summary>
    void SetBasePath(string? basePath);
    
    /// <summary>
    /// Uploads tagged files to blob storage using parallel uploads with retry logic.
    /// Returns only the files that were successfully uploaded for atomic state management.
    /// </summary>
    Task<IReadOnlyList<TaggedFile>> UploadFilesAsync(
        BlobContainerClient containerClient,
        IReadOnlyList<TaggedFile> files,
        CancellationToken cancellationToken);
}
