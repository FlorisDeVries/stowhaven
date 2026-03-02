using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Data;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FluentAssertions;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Unit tests for BackupScanner using mocked dependencies.
/// </summary>
public class BackupScannerTests
{
    private readonly Mock<ILogger<BackupScanner>> _mockLogger = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<IBackupStateService> _mockStateService = new();
    private readonly IOptions<BackupClientOptions> _options;
    private readonly BackupScanner _sut;
    private readonly string _testDirectory;

    public BackupScannerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"scanner-test-{Guid.NewGuid():N}");

        _options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new Dictionary<string, string>
            {
                ["default"] = _testDirectory
            }
        });

        _sut = new BackupScanner(
            _mockFileSystemService.Object,
            _mockStateService.Object,
            _mockLogger.Object);
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(params T[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScanAllTargetsAsync_WhenNoFiles_ShouldReturnEmpty()
    {
        // Arrange
        var targets = new Dictionary<string, string> { ["test"] = "/test/path" };

        _mockFileSystemService.Setup(x => x.ScanDirectoryStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync<FileMetadata>());

        // Act
        var results = new List<TaggedFile>();
        await foreach (var file in _sut.ScanAllTargetsAsync(targets, null, CancellationToken.None))
        {
            results.Add(file);
        }

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScanAllTargetsAsync_WhenMultipleTargets_ShouldScanAllTargets()
    {
        // Arrange
        var targets = new Dictionary<string, string>
        {
            ["target1"] = "/path1",
            ["target2"] = "/path2"
        };

        var file1 = new FileMetadata("/path1/file1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1");
        var file2 = new FileMetadata("/path2/file2.txt", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash2");

        _mockFileSystemService.Setup(x => x.ScanDirectoryStreamAsync(
                "/path1",
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(file1));

        _mockFileSystemService.Setup(x => x.ScanDirectoryStreamAsync(
                "/path2",
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(file2));

        // Act
        var results = new List<TaggedFile>();
        await foreach (var file in _sut.ScanAllTargetsAsync(targets, null, CancellationToken.None))
        {
            results.Add(file);
        }

        // Assert
        results.Should().HaveCount(2);
        results[0].TargetName.Should().Be("target1");
        results[0].TargetDirectory.Should().Be("/path1");
        results[1].TargetName.Should().Be("target2");
        results[1].TargetDirectory.Should().Be("/path2");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScanAllTargetsAsync_WithExcludePatterns_ShouldPassToFileSystemService()
    {
        // Arrange
        var targets = new Dictionary<string, string> { ["test"] = "/test/path" };
        var excludePatterns = new[] { "*.tmp", "*.log" };

        _mockFileSystemService.Setup(x => x.ScanDirectoryStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync<FileMetadata>());

        // Act
        await foreach (var _ in _sut.ScanAllTargetsAsync(targets, excludePatterns, CancellationToken.None))
        {
        }

        // Assert
        _mockFileSystemService.Verify(x => x.ScanDirectoryStreamAsync(
            "/test/path",
            It.Is<string[]?>(p => p != null && p.Length == 2 && p[0] == "*.tmp" && p[1] == "*.log"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenNewFile_ShouldComputeHashAndReturnNew()
    {
        // Arrange
        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/newfile.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), null!));

        _mockStateService.Setup(x => x.GetFileStateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackupFileState?)null);

        _mockFileSystemService.Setup(x => x.ComputeFileHashAsync(
                "/test/path/newfile.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("computed-hash-123");

        // Act
        var (resultFile, needsBackup, changeType) = await _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None);

        // Assert
        needsBackup.Should().BeTrue();
        changeType.Should().Be(FileChangeType.New);
        resultFile.Metadata.Hash.Should().Be("computed-hash-123");
        _mockFileSystemService.Verify(x => x.ComputeFileHashAsync(
            "/test/path/newfile.txt",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenUnchangedFile_ShouldReuseCachedHashWithoutIO()
    {
        // Arrange
        var lastModified = DateTimeOffset.UtcNow.AddDays(-1);
        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/unchanged.txt", 100, lastModified, lastModified.AddDays(-1), null!));

        var previousState = new BackupFileState(
            RelativePath: "test/unchanged.txt",
            Sha256Hash: "cached-hash-123",
            SizeBytes: 100,
            LastModifiedUtc: lastModified,
            BackedUpAt: DateTimeOffset.UtcNow.AddDays(-1),
            BackupRunId: Guid.NewGuid(),
            UniqueFileId: null);

        _mockStateService.Setup(x => x.GetFileStateAsync(
                "test/unchanged.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousState);

        // Act
        var (resultFile, needsBackup, changeType) = await _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None);

        // Assert
        needsBackup.Should().BeFalse();
        changeType.Should().Be(FileChangeType.Unchanged);
        resultFile.Metadata.Hash.Should().Be("cached-hash-123");

        // Critical: Should NOT compute hash for unchanged files (smart hashing optimization)
        _mockFileSystemService.Verify(x => x.ComputeFileHashAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenSizeChanged_ShouldComputeHashAndReturnModified()
    {
        // Arrange
        var lastModified = DateTimeOffset.UtcNow.AddDays(-1);
        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/modified.txt", 200, DateTimeOffset.UtcNow, lastModified, null!));

        var previousState = new BackupFileState(
            RelativePath: "test/modified.txt",
            Sha256Hash: "old-hash",
            SizeBytes: 100, // Different size
            LastModifiedUtc: lastModified,
            BackedUpAt: DateTimeOffset.UtcNow.AddDays(-1),
            BackupRunId: Guid.NewGuid(),
            UniqueFileId: null);

        _mockStateService.Setup(x => x.GetFileStateAsync(
                "test/modified.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousState);

        _mockFileSystemService.Setup(x => x.ComputeFileHashAsync(
                "/test/path/modified.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("new-hash");

        // Act
        var (resultFile, needsBackup, changeType) = await _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None);

        // Assert
        needsBackup.Should().BeTrue();
        changeType.Should().Be(FileChangeType.Modified);
        resultFile.Metadata.Hash.Should().Be("new-hash");
        _mockFileSystemService.Verify(x => x.ComputeFileHashAsync(
            "/test/path/modified.txt",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenTimestampChanged_ShouldComputeHashAndReturnModified()
    {
        // Arrange
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-2);
        var newTimestamp = DateTimeOffset.UtcNow.AddDays(-1);

        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/modified.txt", 100, DateTimeOffset.UtcNow, newTimestamp, null!));

        var previousState = new BackupFileState(
            RelativePath: "test/modified.txt",
            Sha256Hash: "old-hash",
            SizeBytes: 100,
            LastModifiedUtc: oldTimestamp, // Different timestamp
            BackedUpAt: DateTimeOffset.UtcNow.AddDays(-1),
            BackupRunId: Guid.NewGuid(),
            UniqueFileId: null);

        _mockStateService.Setup(x => x.GetFileStateAsync(
                "test/modified.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousState);

        _mockFileSystemService.Setup(x => x.ComputeFileHashAsync(
                "/test/path/modified.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("new-hash");

        // Act
        var (resultFile, needsBackup, changeType) = await _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None);

        // Assert
        needsBackup.Should().BeTrue();
        changeType.Should().Be(FileChangeType.Modified);
        resultFile.Metadata.Hash.Should().Be("new-hash");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenMetadataChangedButContentUnchanged_ShouldReturnUnchanged()
    {
        // Arrange - Edge case: timestamp changed but content is same
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-2);
        var newTimestamp = DateTimeOffset.UtcNow.AddDays(-1);

        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/touched.txt", 100, DateTimeOffset.UtcNow, newTimestamp, null!));

        var previousState = new BackupFileState(
            RelativePath: "test/touched.txt",
            Sha256Hash: "same-hash",
            SizeBytes: 100,
            LastModifiedUtc: oldTimestamp,
            BackedUpAt: DateTimeOffset.UtcNow.AddDays(-1),
            BackupRunId: Guid.NewGuid(),
            UniqueFileId: null);

        _mockStateService.Setup(x => x.GetFileStateAsync(
                "test/touched.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousState);

        _mockFileSystemService.Setup(x => x.ComputeFileHashAsync(
                "/test/path/touched.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("same-hash"); // Content unchanged

        // Act
        var (resultFile, needsBackup, changeType) = await _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None);

        // Assert
        needsBackup.Should().BeFalse();
        changeType.Should().Be(FileChangeType.Unchanged);
        resultFile.Metadata.Hash.Should().Be("same-hash");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectDeletedFilesAsync_WhenNoDeletedFiles_ShouldReturnEmpty()
    {
        // Arrange
        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file1.txt",
            "file2.txt"
        };

        var previousFiles = new List<BackupFileState>
        {
            new("file1.txt", "hash1", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null),
            new("file2.txt", "hash2", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.DetectDeletedFilesAsync(scannedPaths, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectDeletedFilesAsync_WhenFilesDeleted_ShouldReturnDeletedFiles()
    {
        // Arrange
        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file1.txt"
        };

        var previousFiles = new List<BackupFileState>
        {
            new("file1.txt", "hash1", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null),
            new("file2.txt", "hash2", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null),
            new("file3.txt", "hash3", 300, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.DetectDeletedFilesAsync(scannedPaths, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain("file2.txt");
        result.Should().Contain("file3.txt");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DetectDeletedFilesAsync_WhenNoPreviousFiles_ShouldReturnEmpty()
    {
        // Arrange
        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file1.txt"
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupFileState>());

        // Act
        var result = await _sut.DetectDeletedFilesAsync(scannedPaths, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenStateServiceThrows_ShouldPropagateException()
    {
        // Arrange
        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/file.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), null!));

        _mockStateService.Setup(x => x.GetFileStateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenNewFileNotFound_ShouldReturnSkipped()
    {
        // Arrange
        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/missing.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), null!));

        _mockStateService.Setup(x => x.GetFileStateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackupFileState?)null);

        _mockFileSystemService.Setup(x => x.ComputeFileHashAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("File not found", "/test/path/missing.txt"));

        // Act
        var (resultFile, needsBackup, changeType) = await _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None);

        // Assert
        needsBackup.Should().BeFalse();
        changeType.Should().Be(FileChangeType.Skipped);
        resultFile.Metadata.FilePath.Should().Be("/test/path/missing.txt");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenModifiedFileNotFound_ShouldReturnSkipped()
    {
        // Arrange
        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/deleted.txt", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null!));

        var previousState = new BackupFileState(
            RelativePath: "test/deleted.txt",
            Sha256Hash: "old-hash",
            SizeBytes: 100,
            LastModifiedUtc: DateTimeOffset.UtcNow.AddDays(-1),
            BackedUpAt: DateTimeOffset.UtcNow.AddDays(-1),
            BackupRunId: Guid.NewGuid(),
            UniqueFileId: null);

        _mockStateService.Setup(x => x.GetFileStateAsync(
                "test/deleted.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousState);

        _mockFileSystemService.Setup(x => x.ComputeFileHashAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("File deleted", "/test/path/deleted.txt"));

        // Act
        var (resultFile, needsBackup, changeType) = await _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None);

        // Assert
        needsBackup.Should().BeFalse();
        changeType.Should().Be(FileChangeType.Skipped);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenUnauthorizedAccess_ShouldReturnSkipped()
    {
        // Arrange
        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/protected.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), null!));

        _mockStateService.Setup(x => x.GetFileStateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackupFileState?)null);

        _mockFileSystemService.Setup(x => x.ComputeFileHashAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("Access denied"));

        // Act
        var (resultFile, needsBackup, changeType) = await _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None);

        // Assert
        needsBackup.Should().BeFalse();
        changeType.Should().Be(FileChangeType.Skipped);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenIOException_ShouldReturnSkipped()
    {
        // Arrange
        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/locked.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), null!));

        _mockStateService.Setup(x => x.GetFileStateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackupFileState?)null);

        _mockFileSystemService.Setup(x => x.ComputeFileHashAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("File is locked by another process"));

        // Act
        var (resultFile, needsBackup, changeType) = await _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None);

        // Assert
        needsBackup.Should().BeFalse();
        changeType.Should().Be(FileChangeType.Skipped);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnalyzeFileAsync_WhenUnexpectedException_ShouldPropagateException()
    {
        // Arrange
        var taggedFile = new TaggedFile(
            "test",
            "/test/path",
            new FileMetadata("/test/path/file.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), null!));

        _mockStateService.Setup(x => x.GetFileStateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackupFileState?)null);

        _mockFileSystemService.Setup(x => x.ComputeFileHashAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));

        // Act & Assert
        // Other exceptions (not File/IO related) should still propagate
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.AnalyzeFileAsync(taggedFile, CancellationToken.None));
    }
}
