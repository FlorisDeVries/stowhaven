using FluentAssertions;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Integration tests for FileSystemService.
/// Uses real file system with temporary directories for reliable cross-platform testing.
/// </summary>
public class FileSystemServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FileSystemService _sut;

    public FileSystemServiceTests()
    {
        // Create unique temp directory for each test
        _testDirectory = Path.Combine(Path.GetTempPath(), $"backup-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        
        _sut = new FileSystemService(NullLogger<FileSystemService>.Instance);
    }

    public void Dispose()
    {
        // Clean up temp directory after test
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region ScanDirectoryAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithNoFiles_ReturnsEmptyList()
    {
        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithFilesInRootDirectory_FindsAllFiles()
    {
        // Arrange
        CreateTestFile("file1.txt", "content1");
        CreateTestFile("file2.txt", "content2");
        CreateTestFile("file3.log", "content3");

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert
        result.Should().HaveCount(3);
        result.Should().OnlyContain(f => f.FilePath.StartsWith(_testDirectory));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithNestedDirectories_ScansRecursively()
    {
        // Arrange
        CreateTestFile("root.txt", "root");
        CreateTestFile("subdir/file1.txt", "sub1");
        CreateTestFile("subdir/nested/file2.txt", "nested");
        CreateTestFile("another/file3.txt", "another");

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert
        result.Should().HaveCount(4);
        result.Should().Contain(f => f.FilePath.EndsWith("root.txt"));
        result.Should().Contain(f => f.FilePath.Contains("subdir") && f.FilePath.EndsWith("file1.txt"));
        result.Should().Contain(f => f.FilePath.Contains("nested") && f.FilePath.EndsWith("file2.txt"));
        result.Should().Contain(f => f.FilePath.Contains("another") && f.FilePath.EndsWith("file3.txt"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithExcludePattern_ExcludesMatchingFiles()
    {
        // Arrange
        CreateTestFile("keep.txt", "keep");
        CreateTestFile("exclude.tmp", "exclude");
        CreateTestFile("also-keep.log", "keep");
        CreateTestFile("exclude-too.tmp", "exclude");

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory, excludePatterns: ["*.tmp"]);

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(f => !f.FilePath.EndsWith(".tmp"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithMultipleExcludePatterns_ExcludesAllMatches()
    {
        // Arrange
        CreateTestFile("keep.txt", "keep");
        CreateTestFile("exclude.tmp", "exclude");
        CreateTestFile("exclude.log", "exclude");
        CreateTestFile("subdir/exclude.bak", "exclude");

        // Act
        var result = await _sut.ScanDirectoryAsync(
            _testDirectory, 
            excludePatterns: ["*.tmp", "*.log", "*.bak"]);

        // Assert
        result.Should().HaveCount(1);
        result.Single().FilePath.Should().EndWith("keep.txt");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithDirectoryExcludePattern_ExcludesEntireDirectory()
    {
        // Arrange
        CreateTestFile("root.txt", "root");
        CreateTestFile("node_modules/package.json", "package");
        CreateTestFile("node_modules/nested/file.js", "js");
        CreateTestFile("src/code.cs", "code");

        // Act
        var result = await _sut.ScanDirectoryAsync(
            _testDirectory,
            excludePatterns: ["**/node_modules/**"]);

        // Assert
        result.Should().HaveCount(2);
        result.Should().NotContain(f => f.FilePath.Contains("node_modules"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithNonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "does-not-exist");

        // Act
        var act = async () => await _sut.ScanDirectoryAsync(nonExistentPath);

        // Assert
        await act.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage($"Directory not found: {nonExistentPath}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithCancellationToken_StopsScanningWhenCancelled()
    {
        // Arrange - create many files to ensure cancellation happens during scan
        for (int i = 0; i < 100; i++)
        {
            CreateTestFile($"file{i}.txt", $"content{i}");
        }

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act
        var act = async () => await _sut.ScanDirectoryAsync(_testDirectory, cancellationToken: cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region GetFileStreamAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStreamAsync_WithExistingFile_ReturnsReadableStream()
    {
        // Arrange
        var filePath = CreateTestFile("test.txt", "Hello, World!");

        // Act
        await using var stream = await _sut.GetFileStreamAsync(filePath);

        // Assert
        stream.Should().NotBeNull();
        stream.CanRead.Should().BeTrue();
        
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("Hello, World!");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStreamAsync_WithNonExistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "does-not-exist.txt");

        // Act
        var act = async () => await _sut.GetFileStreamAsync(nonExistentPath);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>()
            .WithMessage($"*{nonExistentPath}*");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStreamAsync_WithLargeFile_HandlesEfficientlyWithBuffer()
    {
        // Arrange - create 1MB file
        var filePath = CreateTestFile("large.bin", new string('x', 1024 * 1024));

        // Act
        await using var stream = await _sut.GetFileStreamAsync(filePath);

        // Assert
        stream.Should().NotBeNull();
        stream.Length.Should().Be(1024 * 1024);
    }

    #endregion

    #region ComputeFileHashAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_WithSameContent_ReturnsSameHash()
    {
        // Arrange
        var file1 = CreateTestFile("file1.txt", "identical content");
        var file2 = CreateTestFile("file2.txt", "identical content");

        // Act
        var hash1 = await _sut.ComputeFileHashAsync(file1);
        var hash2 = await _sut.ComputeFileHashAsync(file2);

        // Assert
        hash1.Should().Be(hash2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_WithDifferentContent_ReturnsDifferentHash()
    {
        // Arrange
        var file1 = CreateTestFile("file1.txt", "content A");
        var file2 = CreateTestFile("file2.txt", "content B");

        // Act
        var hash1 = await _sut.ComputeFileHashAsync(file1);
        var hash2 = await _sut.ComputeFileHashAsync(file2);

        // Assert
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_ReturnsLowercaseHexString()
    {
        // Arrange
        var filePath = CreateTestFile("test.txt", "test content");

        // Act
        var hash = await _sut.ComputeFileHashAsync(filePath);

        // Assert
        hash.Should().MatchRegex("^[a-f0-9]{64}$", "SHA256 hash should be 64 lowercase hex characters");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_WithNonExistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "does-not-exist.txt");

        // Act
        var act = async () => await _sut.ComputeFileHashAsync(nonExistentPath);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_WithEmptyFile_ReturnsValidHash()
    {
        // Arrange
        var filePath = CreateTestFile("empty.txt", "");

        // Act
        var hash = await _sut.ComputeFileHashAsync(filePath);

        // Assert
        hash.Should().NotBeNullOrEmpty();
        hash.Length.Should().Be(64);
        // SHA256 of empty string: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_CalledTwice_ReturnsConsistentResult()
    {
        // Arrange
        var filePath = CreateTestFile("test.txt", "consistent hash test");

        // Act
        var hash1 = await _sut.ComputeFileHashAsync(filePath);
        var hash2 = await _sut.ComputeFileHashAsync(filePath);

        // Assert
        hash1.Should().Be(hash2);
    }

    #endregion

    #region GetFileMetadataAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileMetadataAsync_ReturnsCorrectMetadata()
    {
        // Arrange
        var content = "test content for metadata";
        var filePath = CreateTestFile("metadata.txt", content);
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(content);

        // Act
        var metadata = await _sut.GetFileMetadataAsync(filePath);

        // Assert
        metadata.FilePath.Should().Be(filePath);
        metadata.SizeBytes.Should().Be(expectedSize);
        metadata.LastModified.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        metadata.Created.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        metadata.Hash.Should().BeNull("Hash is not computed by GetFileMetadataAsync");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileMetadataAsync_WithNonExistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "does-not-exist.txt");

        // Act
        var act = async () => await _sut.GetFileMetadataAsync(nonExistentPath);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileMetadataAsync_ReturnsUtcTimestamps()
    {
        // Arrange
        var filePath = CreateTestFile("utc-test.txt", "UTC timestamps");

        // Act
        var metadata = await _sut.GetFileMetadataAsync(filePath);

        // Assert
        metadata.LastModified.Offset.Should().Be(TimeSpan.Zero, "LastModified should be UTC");
        metadata.Created.Offset.Should().Be(TimeSpan.Zero, "Created should be UTC");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a test file with the given relative path and content.
    /// Automatically creates parent directories if needed.
    /// </summary>
    private string CreateTestFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_testDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    #endregion
}
