using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.Manifest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Handles parallel file uploads to Azure Blob Storage with retry logic and progress tracking.
/// </summary>
public partial class FileUploader(
    IBackupEncryptionService encryptionService,
    ResiliencePipelineProvider resiliencePipelines,
    IOptions<BackupClientOptions> options,
    ILogger<FileUploader> logger)
    : IFileUploader
{
    private readonly BackupClientOptions _options = options.Value;
    private string? _basePath; // Base path for uploaded blobs (e.g., "staging/device/run/")
    private bool _isPathEmbedded;

    /// <summary>
    /// Sets the base path prefix for uploaded blobs. Must be called before uploading files.
    /// </summary>
    public void SetBasePath(string? basePath, bool isPathEmbedded = false)
    {
        _basePath = basePath?.TrimEnd('/');
        _isPathEmbedded = isPathEmbedded;
    }

    /// <summary>
    /// Uploads tagged files to blob storage using parallel uploads with retry logic.
    /// Returns only the files that were successfully uploaded for atomic state management.
    /// </summary>
    public async Task<UploadBatchResult> UploadFilesAsync(
        BlobContainerClient containerClient,
        IReadOnlyList<TaggedFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return new UploadBatchResult([], [], 0);

        LogUploadingFiles(files.Count);

        // Use thread-safe collections for tracking without locks
        var uploadedFiles = new ConcurrentBag<TaggedFile>();
        var sasExpiredFiles = new ConcurrentBag<TaggedFile>();
        var otherFailureCount = 0;
        var uploadedCount = 0;
        using var throttler = new SemaphoreSlim(_options.MaxParallelUploads, _options.MaxParallelUploads);

        var uploadTasks = files.Select(async taggedFile =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var uploadedFile = await UploadSingleFileAsync(containerClient, taggedFile, cancellationToken);

                // Track successfully uploaded file (ConcurrentBag is thread-safe)
                uploadedFiles.Add(uploadedFile);
                var currentCount = Interlocked.Increment(ref uploadedCount);

                if (currentCount % 10 == 0 || currentCount == files.Count)
                {
                    LogUploadProgress(currentCount, files.Count);
                }
            }
            catch (Exception ex)
            {
                // Log but don't throw - we want to continue uploading other files.
                // A SAS-expiry failure is not counted as a real failure here: the caller can
                // refresh the token and retry these files.
                LogFileUploadFailed(taggedFile.Metadata.FilePath, ex);
                if (IsSasExpiredError(ex))
                {
                    sasExpiredFiles.Add(taggedFile);
                }
                else
                {
                    Interlocked.Increment(ref otherFailureCount);
                }
            }
            finally
            {
                throttler.Release();
            }
        }).ToList();

        await Task.WhenAll(uploadTasks);

        var totalFailures = sasExpiredFiles.Count + otherFailureCount;
        if (totalFailures > 0)
        {
            LogUploadSummary(uploadedFiles.Count, totalFailures, files.Count);
        }
        else
        {
            LogUploadComplete(files.Count);
        }

        return new UploadBatchResult(uploadedFiles.ToList(), sasExpiredFiles.ToList(), otherFailureCount);
    }

    /// <summary>
    /// A 403 AuthenticationFailed from storage during upload means the SAS token's validity window
    /// has passed (the "Signature not valid in the specified time frame" case) — a recoverable
    /// condition that a fresh token resolves, as opposed to a genuine authorization problem.
    /// </summary>
    private static bool IsSasExpiredError(Exception ex)
        => ex is RequestFailedException { Status: 403, ErrorCode: "AuthenticationFailed" }
           || (ex is AggregateException ae && ae.InnerExceptions.Any(IsSasExpiredError));

    /// <summary>
    /// Uploads a single file with Polly resilience pipeline for automatic retry with exponential backoff.
    /// </summary>
    private async Task<TaggedFile> UploadSingleFileAsync(
        BlobContainerClient containerClient,
        TaggedFile taggedFile,
        CancellationToken cancellationToken)
    {
        TaggedFile? uploadedFile = null;
        var storagePath = taggedFile.UniqueFileId ?? taggedFile.GetStoragePath();
        
        // Prepend base path to create full blob path within container
        // Base path is provided by API (e.g., "staging/device-id/run-id/")
        // Production directory SAS URLs already embed this base path in the client URI.
        var blobPath = string.IsNullOrEmpty(_basePath) || _isPathEmbedded
            ? storagePath 
            : $"{_basePath}/{storagePath}";
            
        var blobClient = containerClient.GetBlobClient(blobPath);

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

            await using var preparedUpload = await encryptionService.PrepareUploadAsync(taggedFile, timeoutCts.Token);
            var uploadFile = preparedUpload.File;
            var uploadSize = uploadFile.GetUploadSizeBytes();

            try
            {
                Progress<long>? progress = null;
                if (uploadSize >= _options.LargeFileThresholdBytes)
                {
                    // Track progress for large files
                    progress = new Progress<long>(bytesTransferred =>
                    {
                        var percentage = uploadSize > 0
                            ? (int)((bytesTransferred * 100) / uploadSize)
                            : 0;
                        LogLargeFileProgress(taggedFile.Metadata.FilePath, bytesTransferred,
                            uploadSize, percentage);
                    });
                }

                var uploadOptions = new BlobUploadOptions
                {
                    ProgressHandler = progress,
                    Metadata = CreateBackupMetadata(uploadFile),
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
                };

                await blobClient.UploadAsync(preparedUpload.Content, uploadOptions, timeoutCts.Token);
            }
            catch (RequestFailedException ex) when (IsAlreadyExistsResponse(ex))
            {
                LogBlobAlreadyExists(taggedFile.Metadata.FilePath, blobPath);
            }

            uploadedFile = uploadFile;
        }, cancellationToken);

        return uploadedFile ?? throw new InvalidOperationException($"Upload did not produce metadata for {taggedFile.GetStoragePath()}");
    }

    private static IDictionary<string, string>? CreateBackupMetadata(TaggedFile taggedFile)
    {
        if (taggedFile.UniqueFileId == null)
        {
            return null;
        }

        var uploadSha256 = taggedFile.GetUploadSha256();
        if (string.IsNullOrWhiteSpace(uploadSha256))
        {
            throw new InvalidOperationException($"Missing SHA-256 hash for {taggedFile.GetStoragePath()}");
        }

        return new Dictionary<string, string>
        {
            [BackupBlobMetadata.Sha256] = uploadSha256,
            [BackupBlobMetadata.UniqueFileId] = taggedFile.UniqueFileId
        };
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Flush threshold for the manifest writer. Keeps pending JSON bounded regardless of how many
    /// entries the run produced.
    /// </summary>
    private const int ManifestFlushThresholdBytes = 64 * 1024;

    /// <summary>Block size the streaming manifest writer buffers before sending a block.</summary>
    private const int ManifestUploadBufferBytes = 4 * 1024 * 1024;

    public async Task UploadRunManifestAsync(
        BlobContainerClient containerClient,
        Guid deviceId,
        Guid runId,
        IAsyncEnumerable<ManifestFileEntry> files,
        IAsyncEnumerable<string> deleted,
        string? basePath,
        bool isPathEmbedded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containerClient);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(deleted);

        var blobName = isPathEmbedded || string.IsNullOrWhiteSpace(basePath)
            ? "run-manifest.json"
            : $"{basePath.TrimEnd('/')}/run-manifest.json";

        // Blocks are staged as the write buffer fills, but the block list is committed exactly once,
        // when the blob stream is disposed. Until then the blob does not exist, so an interrupted
        // write leaves only uncommitted blocks (which storage discards) rather than a partial
        // manifest a later run could mistake for a complete one.
        var blobClient = containerClient.GetBlockBlobClient(blobName);
        var writeOptions = new BlockBlobOpenWriteOptions
        {
            BufferSize = ManifestUploadBufferBytes,
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            OpenConditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
        };

        var fileCount = 0;
        var deletedCount = 0;

        try
        {
            await using var blobStream = await blobClient.OpenWriteAsync(overwrite: true, writeOptions, cancellationToken);

            // The writer is flushed often to keep its buffer small, but a flush must not reach the
            // blob stream: committing the block list per flush would mean thousands of commit calls
            // for a large manifest and would publish the blob before it is complete.
            await using var commitSuppressingStream = new FlushSuppressingStream(blobStream);
            await using var writer = new Utf8JsonWriter(commitSuppressingStream);

            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", RunManifest.CurrentSchemaVersion);
            writer.WriteString("deviceId", deviceId.ToString("N"));
            writer.WriteString("runId", runId.ToString("N"));

            // The two sources are drained one after the other, never interleaved: each may hold a
            // lock on its backing store for the duration of its enumeration.
            writer.WritePropertyName("files");
            writer.WriteStartArray();
            await foreach (var entry in files.WithCancellation(cancellationToken))
            {
                JsonSerializer.Serialize(writer, entry, ManifestJsonOptions);
                fileCount++;
                await FlushIfPendingAsync(writer, cancellationToken);
            }

            writer.WriteEndArray();

            writer.WritePropertyName("deleted");
            writer.WriteStartArray();
            await foreach (var path in deleted.WithCancellation(cancellationToken))
            {
                writer.WriteStringValue(path);
                deletedCount++;
                await FlushIfPendingAsync(writer, cancellationToken);
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);
        }
        catch (RequestFailedException ex) when (IsAlreadyExistsResponse(ex))
        {
            LogRunManifestAlreadyExists(blobName);
            return;
        }

        LogRunManifestStreamed(blobName, fileCount, deletedCount);
    }

    private static async Task FlushIfPendingAsync(Utf8JsonWriter writer, CancellationToken cancellationToken)
    {
        if (writer.BytesPending >= ManifestFlushThresholdBytes)
        {
            await writer.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Passes writes through to the wrapped stream but swallows flushes, and does not dispose it.
    /// <see cref="Utf8JsonWriter"/> flushes its buffer to keep memory bounded; on a block blob write
    /// stream a flush also commits the block list, which must happen once at the end rather than on
    /// every buffer turnover. Ownership of the inner stream stays with the caller, which disposes it
    /// afterwards to perform that single commit.
    /// </summary>
    private sealed class FlushSuppressingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
            => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer)
            => inner.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);

        public override void Flush()
        {
            // Intentionally not forwarded; see the type-level remarks.
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static bool IsAlreadyExistsResponse(RequestFailedException ex)
        => ex.Status is 409 or 412;
}
