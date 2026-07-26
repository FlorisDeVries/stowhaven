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
    /// Streams run-manifest.json to the run manifest location, writing entries straight to the blob
    /// as they are produced. A run covering hundreds of thousands of files therefore costs one write
    /// buffer rather than a fully materialized manifest document.
    /// </summary>
    /// <param name="containerClient">Container holding the run's manifest location.</param>
    /// <param name="deviceId">Device the run belongs to.</param>
    /// <param name="runId">Run being described.</param>
    /// <param name="files">File entries, streamed in the order they should appear.</param>
    /// <param name="deleted">Deleted logical paths, streamed in the order they should appear.</param>
    /// <param name="basePath">Manifest base path, when not already embedded in the container URI.</param>
    /// <param name="isPathEmbedded">Whether <paramref name="basePath"/> is already part of the container URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UploadRunManifestAsync(
        BlobContainerClient containerClient,
        Guid deviceId,
        Guid runId,
        IAsyncEnumerable<ManifestFileEntry> files,
        IAsyncEnumerable<string> deleted,
        string? basePath,
        bool isPathEmbedded,
        CancellationToken cancellationToken);
}
