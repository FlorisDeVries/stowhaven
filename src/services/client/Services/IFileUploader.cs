using Azure.Storage.Blobs;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupContracts.Manifest;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Outcome of uploading a batch of files.
/// </summary>
/// <param name="Uploaded">Files that were successfully uploaded (or already existed).</param>
/// <param name="SasExpiredFiles">Files that failed solely because the SAS token had expired; retryable with a fresh token.</param>
/// <param name="OtherFailureCount">Count of files that failed for any other reason.</param>
public sealed record UploadBatchResult(
    IReadOnlyList<TaggedFile> Uploaded,
    IReadOnlyList<TaggedFile> SasExpiredFiles,
    int OtherFailureCount);

/// <summary>
/// Handles parallel file uploads to Azure Blob Storage with retry logic and progress tracking.
/// </summary>
public interface IFileUploader
{
    /// <summary>
    /// Sets the base path prefix for uploaded blobs (e.g., "staging/device/run/").
    /// Must be called before uploading files.
    /// </summary>
    void SetBasePath(string? basePath, bool isPathEmbedded = false);
    
    /// <summary>
    /// Uploads tagged files to blob storage using parallel uploads with retry logic.
    /// Files that failed because the SAS token expired mid-upload are reported separately
    /// so the caller can refresh the token and retry them rather than treating them as lost.
    /// </summary>
    Task<UploadBatchResult> UploadFilesAsync(
        BlobContainerClient containerClient,
        IReadOnlyList<TaggedFile> files,
        CancellationToken cancellationToken);

    /// <summary>
    /// Uploads run-manifest.json to the run manifest location.
    /// </summary>
    Task UploadRunManifestAsync(
        BlobContainerClient containerClient,
        RunManifest manifest,
        string? basePath,
        bool isPathEmbedded,
        CancellationToken cancellationToken);
}
