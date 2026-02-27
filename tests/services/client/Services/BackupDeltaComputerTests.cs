using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Unit tests for BackupDeltaComputer using mocked dependencies.
/// </summary>
public class BackupDeltaComputerTests
{
    private readonly Mock<ILogger<BackupDeltaComputer>> _mockLogger = new();
    private readonly Mock<IBackupStateService> _mockStateService = new();
    private readonly BackupDeltaComputer _sut;

    public BackupDeltaComputerTests()
    {
        _sut = new BackupDeltaComputer(
            _mockStateService.Object,
            _mockLogger.Object);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenNoCurrentFiles_ShouldReturnEmptyDelta()
    {
        // Arrange
        var currentFiles = Array.Empty<FileMetadata>();
        
        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupFileState>());

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.NewFiles.Should().BeEmpty();
        result.ModifiedFiles.Should().BeEmpty();
        result.DeletedFiles.Should().BeEmpty();
        result.TotalBytes.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenNoPreviousFiles_ShouldReturnAllAsNew()
    {
        // Arrange
        var currentFiles = new List<FileMetadata>
        {
            new("/path/file1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"),
            new("/path/file2.txt", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash2")
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupFileState>());

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.NewFiles.Should().HaveCount(2);
        result.NewFiles.Should().Contain(currentFiles[0]);
        result.NewFiles.Should().Contain(currentFiles[1]);
        result.ModifiedFiles.Should().BeEmpty();
        result.DeletedFiles.Should().BeEmpty();
        result.TotalBytes.Should().Be(300); // 100 + 200
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenFilesUnchanged_ShouldReturnEmptyDelta()
    {
        // Arrange
        var currentFiles = new List<FileMetadata>
        {
            new("/path/file1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1")
        };

        var previousFiles = new List<BackupFileState>
        {
            new("/path/file1.txt", "hash1", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.NewFiles.Should().BeEmpty();
        result.ModifiedFiles.Should().BeEmpty();
        result.DeletedFiles.Should().BeEmpty();
        result.TotalBytes.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenFileSizeChanged_ShouldReturnAsModified()
    {
        // Arrange
        var currentFiles = new List<FileMetadata>
        {
            new("/path/file1.txt", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1")
        };

        var previousFiles = new List<BackupFileState>
        {
            new("/path/file1.txt", "hash1", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.NewFiles.Should().BeEmpty();
        result.ModifiedFiles.Should().HaveCount(1);
        result.ModifiedFiles[0].Should().Be(currentFiles[0]);
        result.DeletedFiles.Should().BeEmpty();
        result.TotalBytes.Should().Be(200);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenFileHashChanged_ShouldReturnAsModified()
    {
        // Arrange
        var currentFiles = new List<FileMetadata>
        {
            new("/path/file1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "new-hash")
        };

        var previousFiles = new List<BackupFileState>
        {
            new("/path/file1.txt", "old-hash", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.NewFiles.Should().BeEmpty();
        result.ModifiedFiles.Should().HaveCount(1);
        result.ModifiedFiles[0].Should().Be(currentFiles[0]);
        result.DeletedFiles.Should().BeEmpty();
        result.TotalBytes.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenFileDeleted_ShouldReturnAsDeleted()
    {
        // Arrange
        var currentFiles = Array.Empty<FileMetadata>();

        var previousFiles = new List<BackupFileState>
        {
            new("/path/file1.txt", "hash1", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null),
            new("/path/file2.txt", "hash2", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.NewFiles.Should().BeEmpty();
        result.ModifiedFiles.Should().BeEmpty();
        result.DeletedFiles.Should().HaveCount(2);
        result.DeletedFiles.Should().Contain("/path/file1.txt");
        result.DeletedFiles.Should().Contain("/path/file2.txt");
        result.TotalBytes.Should().Be(0); // Deleted files don't count towards total bytes
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WithMixedChanges_ShouldIdentifyAll()
    {
        // Arrange
        var currentFiles = new List<FileMetadata>
        {
            // New file
            new("/path/new-file.txt", 150, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash-new"),
            // Modified file (size changed)
            new("/path/modified-file.txt", 250, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash-mod"),
            // Unchanged file
            new("/path/unchanged-file.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash-unch")
            // Deleted file is not in current files
        };

        var previousFiles = new List<BackupFileState>
        {
            new("/path/modified-file.txt", "hash-mod", 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null),
            new("/path/unchanged-file.txt", "hash-unch", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null),
            new("/path/deleted-file.txt", "hash-del", 300, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.NewFiles.Should().HaveCount(1);
        result.NewFiles[0].FilePath.Should().Be("/path/new-file.txt");
        
        result.ModifiedFiles.Should().HaveCount(1);
        result.ModifiedFiles[0].FilePath.Should().Be("/path/modified-file.txt");
        
        result.DeletedFiles.Should().HaveCount(1);
        result.DeletedFiles[0].Should().Be("/path/deleted-file.txt");
        
        result.TotalBytes.Should().Be(400); // 150 (new) + 250 (modified)
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenHashNull_ShouldNotConsiderModified()
    {
        // Arrange - Current file has null hash (hash not computed)
        var currentFiles = new List<FileMetadata>
        {
            new("/path/file1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), null)
        };

        var previousFiles = new List<BackupFileState>
        {
            new("/path/file1.txt", "hash1", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert - Should not be marked as modified when hash is null and size is same
        result.NewFiles.Should().BeEmpty();
        result.ModifiedFiles.Should().BeEmpty();
        result.DeletedFiles.Should().BeEmpty();
        result.TotalBytes.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_CaseInsensitivePaths_ShouldMatchCorrectly()
    {
        // Arrange - Different case in paths
        var currentFiles = new List<FileMetadata>
        {
            new("/PATH/FILE1.TXT", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1")
        };

        var previousFiles = new List<BackupFileState>
        {
            new("/path/file1.txt", "hash1", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert - Should match case-insensitively (not treat as new file)
        result.NewFiles.Should().BeEmpty();
        result.ModifiedFiles.Should().BeEmpty();
        result.DeletedFiles.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WithLargeNumberOfFiles_ShouldComputeCorrectly()
    {
        // Arrange - Create 1000 files
        var currentFiles = Enumerable.Range(1, 1000)
            .Select(i => new FileMetadata(
                $"/path/file{i}.txt", 
                i * 100, 
                DateTimeOffset.UtcNow, 
                DateTimeOffset.UtcNow.AddDays(-1), 
                $"hash{i}"))
            .ToList();

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupFileState>());

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.NewFiles.Should().HaveCount(1000);
        result.ModifiedFiles.Should().BeEmpty();
        result.DeletedFiles.Should().BeEmpty();
        
        // Total bytes: sum of 100 + 200 + ... + 100000 = 100 * (1 + 2 + ... + 1000) = 100 * 500500
        var expectedBytes = Enumerable.Range(1, 1000).Sum(i => i * 100L);
        result.TotalBytes.Should().Be(expectedBytes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenStateServiceThrows_ShouldPropagateException()
    {
        // Arrange
        var currentFiles = new List<FileMetadata>
        {
            new("/path/file1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1")
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenCancelled_ShouldPropagateCancellation()
    {
        // Arrange
        var currentFiles = new List<FileMetadata>
        {
            new("/path/file1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1")
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Setup to throw when cancelled
        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.ComputeDeltaAsync(currentFiles, cts.Token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenMultipleFilesWithSameHash_ShouldHandleCorrectly()
    {
        // Arrange - Two different files with same content/hash (duplicates)
        var currentFiles = new List<FileMetadata>
        {
            new("/path/file1.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "same-hash"),
            new("/path/file2.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "same-hash")
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupFileState>());

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert - Both should be treated as new files
        result.NewFiles.Should().HaveCount(2);
        result.TotalBytes.Should().Be(200);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenAllFilesNew_ShouldCalculateTotalBytesCorrectly()
    {
        // Arrange
        var currentFiles = new List<FileMetadata>
        {
            new("/path/file1.txt", 1024, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash1"),
            new("/path/file2.txt", 2048, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash2"),
            new("/path/file3.txt", 4096, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), "hash3")
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupFileState>());

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.TotalBytes.Should().Be(7168); // 1024 + 2048 + 4096
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ComputeDeltaAsync_WhenOnlyDeleted_ShouldNotCountTowardsBytes()
    {
        // Arrange - No current files, only previous files
        var currentFiles = Array.Empty<FileMetadata>();

        var previousFiles = new List<BackupFileState>
        {
            new("/path/file1.txt", "hash1", 5000, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Guid.NewGuid(), null)
        };

        _mockStateService.Setup(x => x.GetAllFileStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousFiles);

        // Act
        var result = await _sut.ComputeDeltaAsync(currentFiles, CancellationToken.None);

        // Assert
        result.DeletedFiles.Should().HaveCount(1);
        result.TotalBytes.Should().Be(0); // Deleted files don't count towards bytes to backup
    }
}
