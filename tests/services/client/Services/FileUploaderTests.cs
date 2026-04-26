using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupContracts.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FluentAssertions;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Unit tests for FileUploader using mocked dependencies.
/// </summary>
public class FileUploaderTests
{
    private readonly Mock<ILogger<FileUploader>> _mockLogger = new();
    private readonly Mock<ILogger<ResiliencePipelineProvider>> _mockResilienceLogger = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<ILogger<BackupEncryptionService>> _mockEncryptionLogger = new();
    private readonly IOptions<BackupClientOptions> _options;
    private readonly ResiliencePipelineProvider _resiliencePipelines;
    private readonly BackupEncryptionService _encryptionService;
    private readonly FileUploader _sut;

    public FileUploaderTests()
    {
        _options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new Dictionary<string, string> { ["test"] = "/test/path" },
            MaxParallelUploads = 2,
            MaxRetryAttempts = 3,
            RetryDelayMs = 100,
            MaxRetryDelayMs = 1000,
            BlobUploadTimeoutSeconds = 300,
            LargeFileThresholdBytes = 100 * 1024 * 1024 // 100 MB
        });

        _resiliencePipelines = new ResiliencePipelineProvider(_options, _mockResilienceLogger.Object);
        _encryptionService = new BackupEncryptionService(
            _mockFileSystemService.Object,
            _options,
            _mockEncryptionLogger.Object);

        _sut = new FileUploader(
            _encryptionService,
            _resiliencePipelines,
            _options,
            _mockLogger.Object);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WhenEmptyList_ShouldReturnEmpty()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var files = Array.Empty<TaggedFile>();

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, files, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WhenSingleFile_ShouldUploadSuccessfully()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();
        var mockResponse = new Mock<Response<BlobContentInfo>>();

        var file = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/file.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"));

        var files = new[] { file };

        mockContainer.Setup(x => x.GetBlobClient("test/file.txt"))
            .Returns(mockBlobClient.Object);

        var memoryStream = new MemoryStream(new byte[100]);
        _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                "/test/path/file.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryStream);

        mockBlobClient.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, files, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].Should().Be(WithUploadMetadata(file));
        mockBlobClient.Verify(x => x.UploadAsync(
            It.IsAny<Stream>(),
            It.Is<BlobUploadOptions>(o => o.Conditions != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WhenMultipleFiles_ShouldUploadAll()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var mockResponse = new Mock<Response<BlobContentInfo>>();

        var files = new[]
        {
            new TaggedFile("test", "/test/path",
                new FileMetadata("/test/path/file1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1")),
            new TaggedFile("test", "/test/path",
                new FileMetadata("/test/path/file2.txt", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash2")),
            new TaggedFile("test", "/test/path",
                new FileMetadata("/test/path/file3.txt", 300, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash3"))
        };

        foreach (var file in files)
        {
            var mockBlobClient = new Mock<BlobClient>();
            var fileName = Path.GetFileName(file.Metadata.FilePath);
            
            mockContainer.Setup(x => x.GetBlobClient($"test/{fileName}"))
                .Returns(mockBlobClient.Object);

            var memoryStream = new MemoryStream(new byte[file.Metadata.SizeBytes]);
            _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                    file.Metadata.FilePath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(memoryStream);

            mockBlobClient.Setup(x => x.UploadAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<BlobUploadOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockResponse.Object);
        }

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, files, CancellationToken.None);

        // Assert
        result.Should().HaveCount(3);
        result.Should().Contain(files.Select(WithUploadMetadata));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WhenOneFileFails_ShouldReturnOnlySuccessfulUploads()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var mockResponse = new Mock<Response<BlobContentInfo>>();

        var file1 = new TaggedFile("test", "/test/path",
            new FileMetadata("/test/path/success.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"));
        var file2 = new TaggedFile("test", "/test/path",
            new FileMetadata("/test/path/fail.txt", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash2"));

        var files = new[] { file1, file2 };

        // Setup successful file
        var mockBlobClient1 = new Mock<BlobClient>();
        mockContainer.Setup(x => x.GetBlobClient("test/success.txt"))
            .Returns(mockBlobClient1.Object);
        
        var memoryStream1 = new MemoryStream(new byte[100]);
        _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                "/test/path/success.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryStream1);

        mockBlobClient1.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        // Setup failing file
        var mockBlobClient2 = new Mock<BlobClient>();
        mockContainer.Setup(x => x.GetBlobClient("test/fail.txt"))
            .Returns(mockBlobClient2.Object);

        _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                "/test/path/fail.txt",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("File locked"));

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, files, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].Should().Be(WithUploadMetadata(file1));
        result.Should().NotContain(file2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WhenAllFilesFail_ShouldReturnEmpty()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();

        var files = new[]
        {
            new TaggedFile("test", "/test/path",
                new FileMetadata("/test/path/fail1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1")),
            new TaggedFile("test", "/test/path",
                new FileMetadata("/test/path/fail2.txt", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash2"))
        };

        foreach (var file in files)
        {
            var mockBlobClient = new Mock<BlobClient>();
            var fileName = Path.GetFileName(file.Metadata.FilePath);
            
            mockContainer.Setup(x => x.GetBlobClient($"test/{fileName}"))
                .Returns(mockBlobClient.Object);

            _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                    file.Metadata.FilePath,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("File locked"));
        }

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, files, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WhenCancelled_ShouldPropagateCancellation()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();

        var file = new TaggedFile("test", "/test/path",
            new FileMetadata("/test/path/file.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"));

        mockContainer.Setup(x => x.GetBlobClient("test/file.txt"))
            .Returns(mockBlobClient.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.UploadFilesAsync(mockContainer.Object, new[] { file }, cts.Token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WithLargeFile_ShouldUseProgressTracking()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();
        var mockResponse = new Mock<Response<BlobContentInfo>>();

        var largeFileSize = 150 * 1024 * 1024; // 150 MB (above threshold)
        var file = new TaggedFile("test", "/test/path",
            new FileMetadata("/test/path/largefile.bin", largeFileSize, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"));

        mockContainer.Setup(x => x.GetBlobClient("test/largefile.bin"))
            .Returns(mockBlobClient.Object);

        var memoryStream = new MemoryStream(new byte[largeFileSize]);
        _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                "/test/path/largefile.bin",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryStream);

        mockBlobClient.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, new[] { file }, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        mockBlobClient.Verify(x => x.UploadAsync(
            It.IsAny<Stream>(),
            It.Is<BlobUploadOptions>(o => o.ProgressHandler != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WithSmallFile_ShouldUseSimpleUpload()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();
        var mockResponse = new Mock<Response<BlobContentInfo>>();

        var smallFileSize = 1024; // 1 KB (well below threshold)
        var file = new TaggedFile("test", "/test/path",
            new FileMetadata("/test/path/smallfile.txt", smallFileSize, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"));

        mockContainer.Setup(x => x.GetBlobClient("test/smallfile.txt"))
            .Returns(mockBlobClient.Object);

        var memoryStream = new MemoryStream(new byte[smallFileSize]);
        _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                "/test/path/smallfile.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryStream);

        mockBlobClient.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, new[] { file }, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        mockBlobClient.Verify(x => x.UploadAsync(
            It.IsAny<Stream>(),
            It.Is<BlobUploadOptions>(o => o.Conditions != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WithUniqueFileId_ShouldUploadCreateOnlyWithSha256Metadata()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();
        var mockResponse = new Mock<Response<BlobContentInfo>>();

        var file = new TaggedFile("test", "/test/path",
            new FileMetadata("/test/path/smallfile.txt", 1024, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"))
        {
            UniqueFileId = "hash1_20260426T120000Z_abc123"
        };

        mockContainer.Setup(x => x.GetBlobClient(file.UniqueFileId))
            .Returns(mockBlobClient.Object);

        var memoryStream = new MemoryStream(new byte[1024]);
        _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                "/test/path/smallfile.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryStream);

        mockBlobClient.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, new[] { file }, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        mockBlobClient.Verify(x => x.UploadAsync(
            It.IsAny<Stream>(),
            It.Is<BlobUploadOptions>(o =>
                o.Conditions != null &&
                o.Metadata != null &&
                o.Metadata[BackupBlobMetadata.Sha256] == "hash1" &&
                o.Metadata[BackupBlobMetadata.UniqueFileId] == file.UniqueFileId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WhenBlobClientThrows_ShouldCatchAndContinue()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var mockResponse = new Mock<Response<BlobContentInfo>>();

        var file1 = new TaggedFile("test", "/test/path",
            new FileMetadata("/test/path/success.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"));
        var file2 = new TaggedFile("test", "/test/path",
            new FileMetadata("/test/path/fail.txt", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash2"));

        // Setup successful file
        var mockBlobClient1 = new Mock<BlobClient>();
        mockContainer.Setup(x => x.GetBlobClient("test/success.txt"))
            .Returns(mockBlobClient1.Object);
        
        var memoryStream1 = new MemoryStream(new byte[100]);
        _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                "/test/path/success.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryStream1);

        mockBlobClient1.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        // Setup failing file with Azure RequestFailedException
        var mockBlobClient2 = new Mock<BlobClient>();
        mockContainer.Setup(x => x.GetBlobClient("test/fail.txt"))
            .Returns(mockBlobClient2.Object);

        var memoryStream2 = new MemoryStream(new byte[200]);
        _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                "/test/path/fail.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryStream2);

        mockBlobClient2.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException("Network error"));

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, new[] { file1, file2 }, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].Should().Be(WithUploadMetadata(file1));
    }

    private static TaggedFile WithUploadMetadata(TaggedFile file) => file with
    {
        UploadSha256 = file.Metadata.Hash,
        UploadSizeBytes = file.Metadata.SizeBytes
    };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UploadFilesAsync_WithStoragePath_ShouldUseCorrectBlobName()
    {
        // Arrange
        var mockContainer = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();
        var mockResponse = new Mock<Response<BlobContentInfo>>();

        var file = new TaggedFile("documents", "/home/user/documents",
            new FileMetadata("/home/user/documents/subfolder/report.pdf", 1000, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"));

        // Expected storage path: "documents/subfolder/report.pdf"
        mockContainer.Setup(x => x.GetBlobClient("documents/subfolder/report.pdf"))
            .Returns(mockBlobClient.Object);

        var memoryStream = new MemoryStream(new byte[1000]);
        _mockFileSystemService.Setup(x => x.GetFileStreamAsync(
                "/home/user/documents/subfolder/report.pdf",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryStream);

        mockBlobClient.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        // Act
        var result = await _sut.UploadFilesAsync(mockContainer.Object, new[] { file }, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        mockContainer.Verify(x => x.GetBlobClient("documents/subfolder/report.pdf"), Times.Once);
    }
}
