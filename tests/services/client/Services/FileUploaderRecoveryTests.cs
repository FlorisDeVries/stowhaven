using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FlorisDeV.BackupClient.Tests.Services;

public partial class FileUploaderTests
{
    [Fact]
    public async Task ParallelEncryptionPreparation_InitializesOneRecoveryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"encryption-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.txt");
            await File.WriteAllTextAsync(source, "parallel first-use encryption");
            var options = Options.Create(new BackupClientOptions
            {
                BackupTargets = new(), Encryption = new()
                {
                    Mode = BackupEncryptionMode.ClientAndServer, KdfIterations = 1000,
                    RecoveryPhraseFilePath = Path.Combine(directory, "phrase.json")
                }
            });
            var fs = new FileSystemService(NullLogger<FileSystemService>.Instance);
            var encryption = new BackupEncryptionService(fs, options, NullLogger<BackupEncryptionService>.Instance);
            var metadata = await fs.GetFileMetadataAsync(source);
            var file = new TaggedFile("target", directory, metadata with { Hash = await fs.ComputeFileHashAsync(source) });
            await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
            {
                await using var prepared = await encryption.PrepareUploadAsync(file);
                var encrypted = Path.Combine(directory, $"stored-{index}.bin");
                await using (var destination = File.Create(encrypted)) { await prepared.Content.CopyToAsync(destination); }
                var restored = Path.Combine(directory, $"restored-{index}.txt");
                await encryption.DecryptFileAsync(encrypted, restored, prepared.File.Encryption!);
                Assert.Equal(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(restored));
            })));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task EncryptedUpload_WhenSuccessResponseIsLost_RetriesOriginalBytesAndRestores()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"upload-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.txt");
            await File.WriteAllTextAsync(source, "Content must survive a lost upload response.");
            var options = Options.Create(new BackupClientOptions
            {
                BackupTargets = new(), RetryDelayMs = 1, MaxRetryDelayMs = 2,
                Encryption = new() { Mode = BackupEncryptionMode.ClientAndServer, KdfIterations = 1000,
                    RecoveryPhraseFilePath = Path.Combine(directory, "phrase.json") }
            });
            var fs = new FileSystemService(NullLogger<FileSystemService>.Instance);
            var encryption = new BackupEncryptionService(fs, options, NullLogger<BackupEncryptionService>.Instance);
            var metadata = await fs.GetFileMetadataAsync(source);
            var file = new TaggedFile("target", directory, metadata with { Hash = await fs.ComputeFileHashAsync(source) })
                { UniqueFileId = "unique-version" };
            byte[]? stored = null;
            var attempts = 0;
            var blob = new Mock<BlobClient>();
            blob.Setup(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
                .Returns<Stream, BlobUploadOptions, CancellationToken>(async (content, _, token) =>
                {
                    using var memory = new MemoryStream();
                    await content.CopyToAsync(memory, token);
                    var bytes = memory.ToArray();
                    if (++attempts == 1)
                    {
                        stored = bytes;
                        throw new TimeoutException("The write succeeded but its response was lost.");
                    }
                    Assert.Equal(stored, bytes);
                    throw new RequestFailedException(409, "Already exists", "BlobAlreadyExists", null);
                });
            var container = new Mock<BlobContainerClient>();
            container.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
            var uploader = new FileUploader(encryption,
                new ResiliencePipelineProvider(options, NullLogger<ResiliencePipelineProvider>.Instance),
                options, NullLogger<FileUploader>.Instance);

            var result = await uploader.UploadFilesAsync(container.Object, [file], CancellationToken.None);
            var uploaded = Assert.Single(result.Uploaded);
            Assert.Equal(2, attempts);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(stored!)).ToLowerInvariant(), uploaded.GetUploadSha256());
            var encrypted = Path.Combine(directory, "stored.bin");
            var restored = Path.Combine(directory, "restored.txt");
            await File.WriteAllBytesAsync(encrypted, stored!);
            await encryption.DecryptFileAsync(encrypted, restored, uploaded.Encryption!);
            Assert.Equal(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(restored));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
