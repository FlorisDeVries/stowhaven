using System.Collections.Concurrent;
using Azure.Storage.Blobs;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Handles parallel file uploads to Azure Blob Storage with retry logic and progress tracking.
/// </summary>
public partial class FileUploader(
    IFileSystemService fileSystemService,
    ResiliencePipelineProvider resiliencePipelines,
    IOptions<BackupClientOptions> options,
    ILogger<FileUploader> logger)
    : IFileUploader
{
    private readonly BackupClientOptions _options = options.Value;

    /// <summary>
    /// Uploads tagged files to blob storage using parallel uploads with retry logic.
    /// Returns only the files that were successfully uploaded for atomic state management.
    /// </summary>
    public async Task<IReadOnlyList<TaggedFile>> UploadFilesAsync(
        BlobContainerClient containerClient,
        IReadOnlyList<TaggedFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return [];

        LogUploadingFiles(files.Count);

        // Use ConcurrentBag for thread-safe file tracking without locks
        var uploadedFiles = new ConcurrentBag<TaggedFile>();
        var uploadedCount = 0;
        using var throttler = new SemaphoreSlim(_options.MaxParallelUploads, _options.MaxParallelUploads);

        var uploadTasks = files.Select(async taggedFile =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                await UploadSingleFileAsync(containerClient, taggedFile, cancellationToken);

                // Track successfully uploaded file (ConcurrentBag is thread-safe)
                uploadedFiles.Add(taggedFile);
                var currentCount = Interlocked.Increment(ref uploadedCount);

                if (currentCount % 10 == 0 || currentCount == files.Count)
                {
                    LogUploadProgress(currentCount, files.Count);
                }

                return (success: true, file: taggedFile, error: (Exception?)null);
            }
            catch (Exception ex)
            {
                // Log but don't throw - we want to continue uploading other files
                LogFileUploadFailed(taggedFile.Metadata.FilePath, ex);
                return (success: false, file: taggedFile, error: ex);
            }
            finally
            {
                throttler.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(uploadTasks);

        var failures = results.Where(r => !r.success).ToList();
        if (failures.Count > 0)
        {
            LogUploadSummary(uploadedFiles.Count, failures.Count, files.Count);
        }
        else
        {
            LogUploadComplete(files.Count);
        }

        return uploadedFiles.ToList();
    }

    /// <summary>
    /// Uploads a single file with Polly resilience pipeline for automatic retry with exponential backoff.
    /// </summary>
    private async Task UploadSingleFileAsync(
        BlobContainerClient containerClient,
        TaggedFile taggedFile,
        CancellationToken cancellationToken)
    {
        var storagePath = taggedFile.GetStoragePath();
        var blobClient = containerClient.GetBlobClient(storagePath);

        // Warn about potentially long-running uploads based on file size
        // Assume ~10 MB/s as reasonable upload speed; warn if estimated time > 50% of timeout
        const long assumedBytesPerSecond = 10 * 1024 * 1024; // 10 MB/s
        var estimatedSeconds = taggedFile.Metadata.SizeBytes / assumedBytesPerSecond;
        var timeoutThreshold = _options.BlobUploadTimeoutSeconds * 0.5;

        if (estimatedSeconds > timeoutThreshold)
        {
            LogLargeFileTimeoutWarning(
                taggedFile.Metadata.FilePath,
                taggedFile.Metadata.SizeBytes,
                estimatedSeconds,
                _options.BlobUploadTimeoutSeconds);
        }

        await resiliencePipelines.BlobUploadPipeline.ExecuteAsync(async ct =>
        {
            // Create timeout for this upload attempt
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.BlobUploadTimeoutSeconds));

            // Get file stream
            await using var fileStream = await fileSystemService.GetFileStreamAsync(
                taggedFile.Metadata.FilePath, timeoutCts.Token);

            if (taggedFile.Metadata.SizeBytes >= _options.LargeFileThresholdBytes)
            {
                // Track progress for large files
                var progress = new Progress<long>(bytesTransferred =>
                {
                    var percentage = taggedFile.Metadata.SizeBytes > 0
                        ? (int)((bytesTransferred * 100) / taggedFile.Metadata.SizeBytes)
                        : 0;
                    LogLargeFileProgress(taggedFile.Metadata.FilePath, bytesTransferred,
                        taggedFile.Metadata.SizeBytes, percentage);
                });

                var uploadOptions = new Azure.Storage.Blobs.Models.BlobUploadOptions
                {
                    ProgressHandler = progress
                };

                await blobClient.UploadAsync(fileStream, uploadOptions, timeoutCts.Token);
            }
            else
            {
                // Regular upload for smaller files
                await blobClient.UploadAsync(fileStream, overwrite: true, timeoutCts.Token);
            }
        }, cancellationToken);
    }
}
