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
        for (var i = 0; i < 100; i++)
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

    #region ScanDirectoryStreamAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_WithNoFiles_ReturnsEmptyStream()
    {
        // Act
        var results = new List<FileMetadata>();
        await foreach (var file in _sut.ScanDirectoryStreamAsync(_testDirectory))
        {
            results.Add(file);
        }

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_WithFilesInRootDirectory_StreamsAllFiles()
    {
        // Arrange
        CreateTestFile("file1.txt", "content1");
        CreateTestFile("file2.txt", "content2");
        CreateTestFile("file3.log", "content3");

        // Act
        var results = new List<FileMetadata>();
        await foreach (var file in _sut.ScanDirectoryStreamAsync(_testDirectory))
        {
            results.Add(file);
        }

        // Assert
        results.Should().HaveCount(3);
        results.Should().OnlyContain(f => f.FilePath.StartsWith(_testDirectory));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_WithNestedDirectories_StreamsRecursively()
    {
        // Arrange
        CreateTestFile("root.txt", "root");
        CreateTestFile("subdir/file1.txt", "sub1");
        CreateTestFile("subdir/nested/file2.txt", "nested");
        CreateTestFile("another/file3.txt", "another");

        // Act
        var results = new List<FileMetadata>();
        await foreach (var file in _sut.ScanDirectoryStreamAsync(_testDirectory))
        {
            results.Add(file);
        }

        // Assert
        results.Should().HaveCount(4);
        results.Should().Contain(f => f.FilePath.EndsWith("root.txt"));
        results.Should().Contain(f => f.FilePath.Contains("subdir") && f.FilePath.EndsWith("file1.txt"));
        results.Should().Contain(f => f.FilePath.Contains("nested") && f.FilePath.EndsWith("file2.txt"));
        results.Should().Contain(f => f.FilePath.Contains("another") && f.FilePath.EndsWith("file3.txt"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_WithExcludePattern_FiltersMatchingFiles()
    {
        // Arrange
        CreateTestFile("keep.txt", "keep");
        CreateTestFile("exclude.tmp", "exclude");
        CreateTestFile("also-keep.log", "keep");
        CreateTestFile("exclude-too.tmp", "exclude");

        // Act
        var results = new List<FileMetadata>();
        await foreach (var file in _sut.ScanDirectoryStreamAsync(_testDirectory, excludePatterns: ["*.tmp"]))
        {
            results.Add(file);
        }

        // Assert
        results.Should().HaveCount(2);
        results.Should().OnlyContain(f => !f.FilePath.EndsWith(".tmp"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_WithCancellationToken_StopsStreamingWhenCancelled()
    {
        // Arrange - create many files to ensure cancellation happens during scan
        for (var i = 0; i < 100; i++)
        {
            CreateTestFile($"file{i}.txt", $"content{i}");
        }

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act
        var act = async () =>
        {
            await foreach (var file in _sut.ScanDirectoryStreamAsync(_testDirectory, cancellationToken: cts.Token))
            {
                // Should not reach here
            }
        };

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_YieldsFilesOneByOne_ConstantMemory()
    {
        // Arrange - create many files
        for (var i = 0; i < 50; i++)
        {
            CreateTestFile($"file{i}.txt", $"content{i}");
        }

        // Act - process files one at a time without loading all into memory
        var processedCount = 0;
        await foreach (var file in _sut.ScanDirectoryStreamAsync(_testDirectory))
        {
            processedCount++;
            file.Should().NotBeNull();
            file.FilePath.Should().NotBeNullOrEmpty();

            // Simulate processing without storing all files
            if (processedCount % 10 == 0)
            {
                // Could log progress, etc.
            }
        }

        // Assert
        processedCount.Should().Be(50);
    }

    #endregion

    #region Additional Coverage Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_SimpleExtensionPattern_ConvertsToRecursivePattern()
    {
        // Arrange - Test that *.tmp excludes .tmp files at any level
        CreateTestFile("root.tmp", "exclude");
        CreateTestFile("keep.txt", "keep");
        CreateTestFile("subdir/nested.tmp", "exclude");
        CreateTestFile("subdir/deep/another.tmp", "exclude");
        CreateTestFile("subdir/keep.log", "keep");

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory, excludePatterns: ["*.tmp"]);

        // Assert
        result.Should().HaveCount(2);
        result.Should().NotContain(f => f.FilePath.EndsWith(".tmp"), 
            "simple extension patterns should exclude at all nesting levels");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_MetadataPopulatedCorrectly()
    {
        // Arrange
        var content = "test file content";
        var filePath = CreateTestFile("metadata-test.txt", content);
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(content);

        // Act
        var results = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert
        var file = results.Should().ContainSingle().Subject;
        file.FilePath.Should().Be(filePath);
        file.SizeBytes.Should().Be(expectedSize);
        file.LastModified.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        file.Created.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        file.Hash.Should().BeNull("scan operations don't compute hashes");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithComplexGlobPattern_MatchesCorrectly()
    {
        // Arrange
        CreateTestFile("src/Program.cs", "code");
        CreateTestFile("src/Utils.cs", "code");
        CreateTestFile("tests/Test.cs", "test");
        CreateTestFile("obj/Debug/output.dll", "exclude");
        CreateTestFile("bin/Release/app.exe", "exclude");

        // Act
        var result = await _sut.ScanDirectoryAsync(
            _testDirectory,
            excludePatterns: ["**/obj/**", "**/bin/**"]);

        // Assert
        result.Should().HaveCount(3);
        result.Should().OnlyContain(f => !f.FilePath.Contains("obj") && !f.FilePath.Contains("bin"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithFilesContainingSpecialCharacters_HandlesCorrectly()
    {
        // Arrange
        CreateTestFile("file with spaces.txt", "spaces");
        CreateTestFile("file-with-dashes.txt", "dashes");
        CreateTestFile("file_with_underscores.txt", "underscores");
        CreateTestFile("file.multiple.dots.txt", "dots");

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert
        result.Should().HaveCount(4);
        result.Should().Contain(f => f.FilePath.Contains("file with spaces.txt"));
        result.Should().OnlyContain(f => f.SizeBytes > 0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_WithLargeFile_ComputesCorrectly()
    {
        // Arrange - Create 5MB file
        var largeContent = new string('A', 5 * 1024 * 1024);
        var filePath = CreateTestFile("large-hash-test.bin", largeContent);

        // Act
        var hash = await _sut.ComputeFileHashAsync(filePath);

        // Assert
        hash.Should().NotBeNullOrEmpty();
        hash.Length.Should().Be(64);
        hash.Should().MatchRegex("^[a-f0-9]{64}$");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var largeContent = new string('X', 1024 * 1024); // 1MB
        var filePath = CreateTestFile("cancel-hash-test.bin", largeContent);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = async () => await _sut.ComputeFileHashAsync(filePath, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStreamAsync_StreamHasCorrectProperties()
    {
        // Arrange
        var content = "stream properties test";
        var filePath = CreateTestFile("stream-props.txt", content);

        // Act
        await using var stream = await _sut.GetFileStreamAsync(filePath);

        // Assert
        stream.Should().NotBeNull();
        stream.CanRead.Should().BeTrue("stream should be readable");
        stream.CanWrite.Should().BeFalse("stream should be read-only");
        stream.CanSeek.Should().BeTrue("FileStream supports seeking");
        stream.Position.Should().Be(0, "stream should start at beginning");
        stream.Length.Should().Be(System.Text.Encoding.UTF8.GetByteCount(content));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStreamAsync_SupportsSharedRead_AllowsMultipleReaders()
    {
        // Arrange
        var filePath = CreateTestFile("shared-read.txt", "shared read test");

        // Act
        await using var stream1 = await _sut.GetFileStreamAsync(filePath);
        await using var stream2 = await _sut.GetFileStreamAsync(filePath);

        // Assert
        stream1.Should().NotBeNull();
        stream2.Should().NotBeNull();
        
        using var reader1 = new StreamReader(stream1);
        using var reader2 = new StreamReader(stream2);
        
        var content1 = await reader1.ReadToEndAsync();
        var content2 = await reader2.ReadToEndAsync();
        
        content1.Should().Be("shared read test");
        content2.Should().Be("shared read test");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileMetadataAsync_WithZeroByteFile_ReturnsCorrectSize()
    {
        // Arrange
        var filePath = CreateTestFile("zero-byte.txt", "");

        // Act
        var metadata = await _sut.GetFileMetadataAsync(filePath);

        // Assert
        metadata.SizeBytes.Should().Be(0);
        metadata.FilePath.Should().Be(filePath);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileMetadataAsync_WithLargeFile_ReturnsCorrectSize()
    {
        // Arrange
        var content = new string('L', 10 * 1024 * 1024); // 10MB
        var filePath = CreateTestFile("large-metadata.bin", content);
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(content);

        // Act
        var metadata = await _sut.GetFileMetadataAsync(filePath);

        // Assert
        metadata.SizeBytes.Should().Be(expectedSize);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_WithNonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "does-not-exist");

        // Act
        var act = async () =>
        {
            await foreach (var file in _sut.ScanDirectoryStreamAsync(nonExistentPath))
            {
                // Should not reach here
            }
        };

        // Assert
        await act.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage($"Directory not found: {nonExistentPath}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithEmptyExcludePatternArray_IncludesAllFiles()
    {
        // Arrange
        CreateTestFile("file1.txt", "1");
        CreateTestFile("file2.tmp", "2");
        CreateTestFile("file3.log", "3");

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory, excludePatterns: []);

        // Assert
        result.Should().HaveCount(3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithNullExcludePatterns_IncludesAllFiles()
    {
        // Arrange
        CreateTestFile("file1.txt", "1");
        CreateTestFile("file2.tmp", "2");
        CreateTestFile("file3.log", "3");

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory, excludePatterns: null);

        // Assert
        result.Should().HaveCount(3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_MetadataIncludesAllRequiredFields()
    {
        // Arrange
        CreateTestFile("metadata-check.txt", "check all fields");

        // Act
        await foreach (var file in _sut.ScanDirectoryStreamAsync(_testDirectory))
        {
            // Assert
            file.FilePath.Should().NotBeNullOrEmpty();
            file.SizeBytes.Should().BeGreaterThan(0);
            file.LastModified.Should().NotBe(default(DateTimeOffset));
            file.Created.Should().NotBe(default(DateTimeOffset));
            file.LastModified.Offset.Should().Be(TimeSpan.Zero, "should be UTC");
            file.Created.Offset.Should().Be(TimeSpan.Zero, "should be UTC");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_KnownContent_ReturnsExpectedHash()
    {
        // Arrange
        var filePath = CreateTestFile("known-hash.txt", "hello world");

        // Act
        var hash = await _sut.ComputeFileHashAsync(filePath);

        // Assert - SHA256("hello world") from known good tool
        hash.Should().Be("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithVeryDeeplyNestedStructure_ScansCorrectly()
    {
        // Arrange - Create deeply nested structure
        CreateTestFile("level1/level2/level3/level4/level5/deep.txt", "deep");
        CreateTestFile("level1/level2/sibling.txt", "sibling");
        CreateTestFile("level1/root-level.txt", "root");

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert
        result.Should().HaveCount(3);
        result.Should().Contain(f => f.FilePath.Contains("level5"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithMixedExcludePatterns_AppliesBothFileAndDirectory()
    {
        // Arrange
        CreateTestFile("keep.txt", "keep");
        CreateTestFile("exclude.tmp", "exclude-file");
        CreateTestFile("build/output.dll", "exclude-dir");
        CreateTestFile("build/assets/icon.png", "exclude-dir");
        CreateTestFile("src/code.cs", "keep");

        // Act
        var result = await _sut.ScanDirectoryAsync(
            _testDirectory,
            excludePatterns: ["*.tmp", "**/build/**"]);

        // Assert
        result.Should().HaveCount(2);
        result.Should().NotContain(f => f.FilePath.EndsWith(".tmp"));
        result.Should().NotContain(f => f.FilePath.Contains("build"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_ConcurrentCalls_ProduceSameHash()
    {
        // Arrange
        var filePath = CreateTestFile("concurrent-hash.txt", "concurrent test content");

        // Act
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _sut.ComputeFileHashAsync(filePath))
            .ToArray();

        var hashes = await Task.WhenAll(tasks);

        // Assert
        hashes.Should().OnlyContain(h => h == hashes[0], "all concurrent hash computations should match");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStreamAsync_StreamCanBeReadMultipleTimes()
    {
        // Arrange
        var filePath = CreateTestFile("reread.txt", "reread test");

        // Act
        await using var stream = await _sut.GetFileStreamAsync(filePath);
        
        using var reader1 = new StreamReader(stream, leaveOpen: true);
        var firstRead = await reader1.ReadToEndAsync();
        
        stream.Position = 0; // Reset to beginning
        
        using var reader2 = new StreamReader(stream);
        var secondRead = await reader2.ReadToEndAsync();

        // Assert
        firstRead.Should().Be("reread test");
        secondRead.Should().Be("reread test");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_HandlesEmptyDirectories()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_testDirectory, "empty1"));
        Directory.CreateDirectory(Path.Combine(_testDirectory, "empty2", "nested-empty"));
        CreateTestFile("has-content/file.txt", "content");

        // Act
        var results = new List<FileMetadata>();
        await foreach (var file in _sut.ScanDirectoryStreamAsync(_testDirectory))
        {
            results.Add(file);
        }

        // Assert
        results.Should().HaveCount(1, "empty directories should not produce results");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithHiddenFiles_IncludesThem()
    {
        // Arrange
        var hiddenFilePath = CreateTestFile(".hidden", "hidden content");
        var normalFilePath = CreateTestFile("normal.txt", "normal content");
        
        // Mark as hidden on Windows (no-op on Unix)
        try
        {
            File.SetAttributes(hiddenFilePath, FileAttributes.Hidden);
        }
        catch
        {
            // Ignore on non-Windows platforms
        }

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert
        result.Should().HaveCount(2, "hidden files should be included in scan");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_WithBinaryContent_ComputesCorrectly()
    {
        // Arrange
        var binaryContent = new byte[] { 0x00, 0xFF, 0x42, 0xAA, 0x55, 0x01, 0x02, 0x03 };
        var filePath = Path.Combine(_testDirectory, "binary.bin");
        await File.WriteAllBytesAsync(filePath, binaryContent);

        // Act
        var hash = await _sut.ComputeFileHashAsync(filePath);

        // Assert
        hash.Should().NotBeNullOrEmpty();
        hash.Length.Should().Be(64);
        hash.Should().MatchRegex("^[a-f0-9]{64}$");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileMetadataAsync_PreservesFilePathExactly()
    {
        // Arrange
        var expectedPath = CreateTestFile("preserve-path.txt", "test");

        // Act
        var metadata = await _sut.GetFileMetadataAsync(expectedPath);

        // Assert
        metadata.FilePath.Should().Be(expectedPath, "file path should be preserved exactly as provided");
    }

    #endregion

    #region Exception Handling Tests

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("OS", "Unix")]
    public async Task ScanDirectoryAsync_WithUnauthorizedDirectory_SkipsDirectoryAndContinues()
    {
        // Arrange
        CreateTestFile("accessible/file1.txt", "accessible");
        CreateTestFile("restricted/secret.txt", "restricted");
        CreateTestFile("accessible2/file2.txt", "accessible");

        var restrictedDir = Path.Combine(_testDirectory, "restricted");

        // Remove all permissions (no read, write, or execute)
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(restrictedDir, UnixFileMode.None);
            }
            catch
            {
                // Skip test if we can't set permissions (e.g., not running as appropriate user)
                return;
            }

            try
            {
                // Act
                var result = await _sut.ScanDirectoryAsync(_testDirectory);

                // Assert - should skip restricted directory but get other files
                result.Should().HaveCount(2, "should skip inaccessible directory but continue scanning");
                result.Should().OnlyContain(f => !f.FilePath.Contains("restricted"));
            }
            finally
            {
                // Cleanup - restore permissions so disposal can delete the directory
                try
                {
                    File.SetUnixFileMode(restrictedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("OS", "Unix")]
    public async Task ScanDirectoryAsync_WithReadProtectedFile_StillGetsMetadata()
    {
        // Arrange - FileInfo can read metadata even without read permissions
        CreateTestFile("file1.txt", "accessible");
        var restrictedFile = CreateTestFile("restricted.txt", "no-read-access");
        CreateTestFile("file2.txt", "accessible");

        // Remove read permission (but keep execute on directory so we can list it)
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                // Note: On Unix, FileInfo can still get metadata without read permission
                // The exception only occurs when opening the file for reading
                File.SetUnixFileMode(restrictedFile, UnixFileMode.None);
            }
            catch
            {
                return; // Skip test if permissions can't be set
            }

            try
            {
                // Act
                var result = await _sut.ScanDirectoryAsync(_testDirectory);

                // Assert - GetFileMetadataAsync doesn't open file, so it succeeds
                // This test documents current behavior: metadata can be read without file read permission
                result.Should().HaveCount(3, "metadata can be read even without file read permission");
            }
            finally
            {
                try
            {
                    File.SetUnixFileMode(restrictedFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch { }
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryStreamAsync_WithUnauthorizedDirectory_SkipsAndYieldsOtherFiles()
    {
        // Arrange
        CreateTestFile("accessible/file1.txt", "accessible");
        CreateTestFile("restricted/secret.txt", "restricted");
        CreateTestFile("accessible2/file2.txt", "accessible");

        var restrictedDir = Path.Combine(_testDirectory, "restricted");

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(restrictedDir, UnixFileMode.None);
            }
            catch
            {
                return; // Skip if can't set permissions
            }

            try
            {
                // Act
                var results = new List<FileMetadata>();
                await foreach (var file in _sut.ScanDirectoryStreamAsync(_testDirectory))
                {
                    results.Add(file);
                }

                // Assert
                results.Should().HaveCount(2, "should skip inaccessible directory in stream");
                results.Should().OnlyContain(f => !f.FilePath.Contains("restricted"));
            }
            finally
            {
                try
                {
                    File.SetUnixFileMode(restrictedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch { }
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_DocumentsResilience_ToFileSystemChanges()
    {
        // Arrange - This test documents that scanning is resilient to permission issues
        // Note: Actual file permission behavior varies by OS and file system
        CreateTestFile("good1.txt", "accessible");
        CreateTestFile("good2.txt", "accessible");
        CreateTestFile("nested/good3.txt", "accessible");
        CreateTestFile("good4.txt", "accessible");

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert - All accessible files are found
        result.Should().HaveCount(4, "should find all accessible files");
        result.Should().OnlyContain(f => f.FilePath.Contains("good"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_DocumentsResilienceToFileDeletion()
    {
        // Arrange - This test documents behavior when files are deleted
        var files = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            files.Add(CreateTestFile($"file{i}.txt", $"content{i}"));
        }

        // Act - Scan with all files present
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert
        result.Should().HaveCount(10);

        // Delete a file and rescan - demonstrates graceful handling
        File.Delete(files[5]);

        result = await _sut.ScanDirectoryAsync(_testDirectory);
        result.Should().HaveCount(9, "scan continues after file deletion");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithNestedPermissionIssues_SkipsNestedAndContinues()
    {
        // Arrange - Create deeply nested structure with permission issues at various levels
        CreateTestFile("level1/good.txt", "accessible");
        CreateTestFile("level1/level2/good.txt", "accessible");
        CreateTestFile("level1/level2/restricted/secret.txt", "restricted");
        CreateTestFile("level1/level2/level3/good.txt", "accessible");

        var restrictedDir = Path.Combine(_testDirectory, "level1", "level2", "restricted");

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(restrictedDir, UnixFileMode.None);
            }
            catch
            {
                return;
            }

            try
            {
                // Act
                var result = await _sut.ScanDirectoryAsync(_testDirectory);

                // Assert
                result.Should().HaveCount(3, "should skip nested restricted directory");
                result.Should().NotContain(f => f.FilePath.Contains("secret.txt"));
            }
            finally
            {
                try
                {
                    File.SetUnixFileMode(restrictedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch { }
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStreamAsync_WithLockedFile_ThrowsIOException()
    {
        // Arrange
        var filePath = CreateTestFile("locked.txt", "locked content");

        // Lock the file by opening it with exclusive access
        await using (var lockingStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None)) // Exclusive access
        {
            // Act
            var act = async () => await _sut.GetFileStreamAsync(filePath);

            // Assert
            await act.Should().ThrowAsync<IOException>("file is locked by another process");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeFileHashAsync_WithLockedFile_ThrowsIOException()
    {
        // Arrange
        var filePath = CreateTestFile("locked-hash.txt", "locked for hashing");

        // Lock the file
        await using (var lockingStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            // Act
            var act = async () => await _sut.ComputeFileHashAsync(filePath);

            // Assert
            await act.Should().ThrowAsync<IOException>("file is locked");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileMetadataAsync_WithLockedFile_Succeeds()
    {
        // Arrange
        var filePath = CreateTestFile("locked-metadata.txt", "metadata even when locked");

        // Lock the file
        await using (var lockingStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            // Act - GetFileMetadataAsync only reads file info, doesn't open file
            var metadata = await _sut.GetFileMetadataAsync(filePath);

            // Assert
            metadata.Should().NotBeNull("metadata can be read even when file is locked");
            metadata.FilePath.Should().Be(filePath);
            metadata.SizeBytes.Should().BeGreaterThan(0);
        }
    }

    #endregion

    #region Symlink Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithBrokenSymlink_SkipsItSilently()
    {
        // Arrange - a symlink whose target does not exist (e.g. a stale Steam runtime .so link)
        CreateTestFile("real.txt", "content");
        if (!CreateSymlink("dangling.so", target: Path.Combine(_testDirectory, "missing-target.so")))
            return; // platform disallows symlink creation

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert - only the real file is returned; the broken link is not surfaced as an error
        result.Should().ContainSingle();
        result.Single().FilePath.Should().EndWith("real.txt");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithValidSymlink_IncludesTarget()
    {
        // Arrange - a symlink pointing at an existing file should still be backed up
        var target = CreateTestFile("target.txt", "content");
        if (!CreateSymlink("link.txt", target))
            return; // platform disallows symlink creation

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert - both the target and the (valid) link resolve to real files
        result.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScanDirectoryAsync_WithChainedBrokenSymlink_SkipsItSilently()
    {
        // Arrange - a multi-hop link chain whose final target is missing, mirroring Steam's
        // .steampath -> .steam/sdk32/steam -> .../linux32/steam (final target absent).
        CreateTestFile("real.txt", "content");
        if (!CreateSymlink("hop2", target: Path.Combine(_testDirectory, "missing-final")))
            return; // platform disallows symlink creation
        CreateSymlink("hop1", target: Path.Combine(_testDirectory, "hop2"));

        // Act
        var result = await _sut.ScanDirectoryAsync(_testDirectory);

        // Assert - the whole broken chain is skipped, only the real file remains
        result.Should().ContainSingle();
        result.Single().FilePath.Should().EndWith("real.txt");
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

    /// <summary>
    /// Creates a symbolic link at the given relative path pointing at <paramref name="target"/>.
    /// Returns false when the platform disallows symlink creation (e.g. Windows without developer
    /// mode or elevation) so callers can bail out and keep the suite green across environments.
    /// </summary>
    private bool CreateSymlink(string relativePath, string target)
    {
        var linkPath = Path.Combine(_testDirectory, relativePath);
        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    #endregion
}
