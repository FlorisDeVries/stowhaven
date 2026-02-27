using FluentAssertions;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Integration tests for BackupStateService.
/// Uses real SQLite database with temporary files for reliable testing.
/// </summary>
public class BackupStateServiceTests : IDisposable
{
    private readonly string _testDatabasePath;
    private readonly BackupStateService _sut;
    private readonly BackupDeltaComputer _deltaComputer;

    public BackupStateServiceTests()
    {
        // Create unique temp database for each test
        _testDatabasePath = Path.Combine(Path.GetTempPath(), $"backup-test-{Guid.NewGuid()}.db");
        
        var options = Options.Create(new DatabaseOptions { FilePath = _testDatabasePath });
        _sut = new BackupStateService(options, NullLogger<BackupStateService>.Instance);
        
        // Create delta computer that depends on the state service
        _deltaComputer = new BackupDeltaComputer(_sut, NullLogger<BackupDeltaComputer>.Instance);
    }

    public void Dispose()
    {
        // Clean up database
        _sut.Dispose();
        
        if (File.Exists(_testDatabasePath))
        {
            File.Delete(_testDatabasePath);
        }
    }

    #region GetOrCreateDeviceStateAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrCreateDeviceStateAsync_WhenDatabaseEmpty_CreatesNewDeviceState()
    {
        // Act
        var result = await _sut.GetOrCreateDeviceStateAsync();

        // Assert
        result.Should().NotBeNull();
        result.DeviceId.Should().NotBeEmpty();
        result.LastSuccessfulBackup.Should().BeNull();
        result.LastRunId.Should().BeNull();
        result.LastCommitId.Should().BeNull();
        result.TotalFilesTracked.Should().Be(0);
        result.TotalBytesTracked.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrCreateDeviceStateAsync_WhenCalledTwice_ReturnsSameDeviceId()
    {
        // Act
        var firstCall = await _sut.GetOrCreateDeviceStateAsync();
        var secondCall = await _sut.GetOrCreateDeviceStateAsync();

        // Assert
        firstCall.DeviceId.Should().Be(secondCall.DeviceId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetOrCreateDeviceStateAsync_AfterBackupSaved_ReturnsUpdatedState()
    {
        // Arrange
        var initialState = await _sut.GetOrCreateDeviceStateAsync();
        var runId = Guid.NewGuid();
        var commitId = "commit-abc123";
        var files = CreateTestFileMetadata(3);

        await _sut.SaveBackupSuccessAsync(runId, commitId, files);

        // Act
        var updatedState = await _sut.GetOrCreateDeviceStateAsync();

        // Assert
        updatedState.DeviceId.Should().Be(initialState.DeviceId);
        updatedState.LastSuccessfulBackup.Should().NotBeNull();
        updatedState.LastRunId.Should().Be(runId);
        updatedState.LastCommitId.Should().Be(commitId);
        updatedState.TotalFilesTracked.Should().Be(3);
        updatedState.TotalBytesTracked.Should().Be(files.Sum(f => f.SizeBytes));
    }

    #endregion

    #region ComputeDeltaAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeDeltaAsync_WhenNoPreviousBackup_AllFilesAreNew()
    {
        // Arrange
        var currentFiles = CreateTestFileMetadata(5);

        // Act
        var delta = await _deltaComputer.ComputeDeltaAsync(currentFiles);

        // Assert
        delta.NewFiles.Should().HaveCount(5);
        delta.ModifiedFiles.Should().BeEmpty();
        delta.DeletedFiles.Should().BeEmpty();
        delta.TotalBytes.Should().Be(currentFiles.Sum(f => f.SizeBytes));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeDeltaAsync_WhenNoChanges_ReturnsEmptyDelta()
    {
        // Arrange
        var files = CreateTestFileMetadata(3);
        await SaveBackupState(files);

        // Act
        var delta = await _deltaComputer.ComputeDeltaAsync(files);

        // Assert
        delta.NewFiles.Should().BeEmpty();
        delta.ModifiedFiles.Should().BeEmpty();
        delta.DeletedFiles.Should().BeEmpty();
        delta.TotalBytes.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeDeltaAsync_WhenFilesAdded_DetectsNewFiles()
    {
        // Arrange
        var originalFiles = CreateTestFileMetadata(2);
        await SaveBackupState(originalFiles);

        var newFiles = CreateTestFileMetadata(2, startIndex: 2);
        var currentFiles = originalFiles.Concat(newFiles).ToList();

        // Act
        var delta = await _deltaComputer.ComputeDeltaAsync(currentFiles);

        // Assert
        delta.NewFiles.Should().HaveCount(2);
        delta.NewFiles.Should().Contain(f => f.FilePath == newFiles[0].FilePath);
        delta.NewFiles.Should().Contain(f => f.FilePath == newFiles[1].FilePath);
        delta.ModifiedFiles.Should().BeEmpty();
        delta.DeletedFiles.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeDeltaAsync_WhenFileHashChanged_DetectsModification()
    {
        // Arrange
        var originalFiles = CreateTestFileMetadata(3);
        await SaveBackupState(originalFiles);

        // Modify hash of second file
        var modifiedFiles = originalFiles.ToList();
        modifiedFiles[1] = modifiedFiles[1] with { Hash = "modified-hash-xyz" };

        // Act
        var delta = await _deltaComputer.ComputeDeltaAsync(modifiedFiles);

        // Assert
        delta.NewFiles.Should().BeEmpty();
        delta.ModifiedFiles.Should().HaveCount(1);
        delta.ModifiedFiles[0].FilePath.Should().Be(originalFiles[1].FilePath);
        delta.DeletedFiles.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeDeltaAsync_WhenFileSizeChanged_DetectsModification()
    {
        // Arrange
        var originalFiles = CreateTestFileMetadata(3);
        await SaveBackupState(originalFiles);

        // Modify size of first file
        var modifiedFiles = originalFiles.ToList();
        modifiedFiles[0] = modifiedFiles[0] with { SizeBytes = 999999 };

        // Act
        var delta = await _deltaComputer.ComputeDeltaAsync(modifiedFiles);

        // Assert
        delta.NewFiles.Should().BeEmpty();
        delta.ModifiedFiles.Should().HaveCount(1);
        delta.ModifiedFiles[0].FilePath.Should().Be(originalFiles[0].FilePath);
        delta.DeletedFiles.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeDeltaAsync_WhenFilesDeleted_DetectsDeletions()
    {
        // Arrange
        var originalFiles = CreateTestFileMetadata(5);
        await SaveBackupState(originalFiles);

        // Remove 2 files
        var remainingFiles = originalFiles.Take(3).ToList();

        // Act
        var delta = await _deltaComputer.ComputeDeltaAsync(remainingFiles);

        // Assert
        delta.NewFiles.Should().BeEmpty();
        delta.ModifiedFiles.Should().BeEmpty();
        delta.DeletedFiles.Should().HaveCount(2);
        delta.DeletedFiles.Should().Contain(originalFiles[3].FilePath);
        delta.DeletedFiles.Should().Contain(originalFiles[4].FilePath);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeDeltaAsync_WithMixedChanges_DetectsAllChangeTypes()
    {
        // Arrange
        var originalFiles = CreateTestFileMetadata(5);
        await SaveBackupState(originalFiles);

        var currentFiles = new List<FileMetadata>
        {
            originalFiles[0], // Unchanged
            originalFiles[1] with { Hash = "modified-hash" }, // Modified
            originalFiles[2], // Unchanged
            // originalFiles[3] and [4] deleted
            CreateTestFileMetadata(1, startIndex: 10)[0], // New file
            CreateTestFileMetadata(1, startIndex: 11)[0]  // New file
        };

        // Act
        var delta = await _deltaComputer.ComputeDeltaAsync(currentFiles);

        // Assert
        delta.NewFiles.Should().HaveCount(2);
        delta.ModifiedFiles.Should().HaveCount(1);
        delta.DeletedFiles.Should().HaveCount(2);
        delta.TotalBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComputeDeltaAsync_IsCaseInsensitive_ForFilePaths()
    {
        // Arrange
        var originalFiles = new List<FileMetadata>
        {
            new("/path/to/MyFile.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash1")
        };
        await SaveBackupState(originalFiles);

        var currentFiles = new List<FileMetadata>
        {
            new("/path/to/myfile.txt", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash1")
        };

        // Act
        var delta = await _deltaComputer.ComputeDeltaAsync(currentFiles);

        // Assert
        delta.NewFiles.Should().BeEmpty();
        delta.ModifiedFiles.Should().BeEmpty();
        delta.DeletedFiles.Should().BeEmpty();
    }

    #endregion

    #region SaveBackupSuccessAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveBackupSuccessAsync_SavesAllFiles()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var commitId = "commit-xyz789";
        var files = CreateTestFileMetadata(3);

        // Act
        await _sut.SaveBackupSuccessAsync(runId, commitId, files);

        // Assert
        var allFiles = await _sut.GetAllFileStatesAsync();
        allFiles.Should().HaveCount(3);
        allFiles.Should().OnlyContain(f => f.BackupRunId == runId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveBackupSuccessAsync_UpdatesDeviceState()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var commitId = "commit-abc123";
        var files = CreateTestFileMetadata(5);

        // Act
        await _sut.SaveBackupSuccessAsync(runId, commitId, files);

        // Assert
        var deviceState = await _sut.GetOrCreateDeviceStateAsync();
        deviceState.LastRunId.Should().Be(runId);
        deviceState.LastCommitId.Should().Be(commitId);
        deviceState.LastSuccessfulBackup.Should().NotBeNull()
            .And.BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        deviceState.TotalFilesTracked.Should().Be(5);
        deviceState.TotalBytesTracked.Should().Be(files.Sum(f => f.SizeBytes));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveBackupSuccessAsync_OverwritesExistingFiles()
    {
        // Arrange
        var initialFiles = CreateTestFileMetadata(2);
        await SaveBackupState(initialFiles);

        var runId = Guid.NewGuid();
        var commitId = "new-commit";
        var updatedFiles = new List<FileMetadata>
        {
            initialFiles[0] with { Hash = "updated-hash", SizeBytes = 999 },
            initialFiles[1]
        };

        // Act
        await _sut.SaveBackupSuccessAsync(runId, commitId, updatedFiles);

        // Assert
        var fileState = await _sut.GetFileStateAsync(initialFiles[0].FilePath);
        fileState.Should().NotBeNull();
        fileState!.Sha256Hash.Should().Be("updated-hash");
        fileState.SizeBytes.Should().Be(999);
        fileState.BackupRunId.Should().Be(runId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveBackupSuccessAsync_WithEmptyFileList_UpdatesDeviceStateOnly()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var commitId = "empty-commit";
        var emptyFiles = new List<FileMetadata>();

        // Act
        await _sut.SaveBackupSuccessAsync(runId, commitId, emptyFiles);

        // Assert
        var deviceState = await _sut.GetOrCreateDeviceStateAsync();
        deviceState.LastRunId.Should().Be(runId);
        deviceState.LastCommitId.Should().Be(commitId);
        
        var allFiles = await _sut.GetAllFileStatesAsync();
        allFiles.Should().BeEmpty();
    }

    #endregion

    #region GetFileStateAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStateAsync_WhenFileExists_ReturnsFileState()
    {
        // Arrange
        var files = CreateTestFileMetadata(3);
        await SaveBackupState(files);

        // Act
        var result = await _sut.GetFileStateAsync(files[1].FilePath);

        // Assert
        result.Should().NotBeNull();
        result!.RelativePath.Should().Be(files[1].FilePath);
        result.Sha256Hash.Should().Be(files[1].Hash);
        result.SizeBytes.Should().Be(files[1].SizeBytes);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStateAsync_WhenFileDoesNotExist_ReturnsNull()
    {
        // Arrange
        var files = CreateTestFileMetadata(2);
        await SaveBackupState(files);

        // Act
        var result = await _sut.GetFileStateAsync("/non/existent/file.txt");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetFileStateAsync_IsCaseInsensitive()
    {
        // Arrange
        var files = new List<FileMetadata>
        {
            new("/Path/To/MyFile.TXT", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash1")
        };
        await SaveBackupState(files);

        // Act
        var result = await _sut.GetFileStateAsync("/path/to/myfile.txt");

        // Assert
        result.Should().NotBeNull();
        result!.Sha256Hash.Should().Be("hash1");
    }

    #endregion

    #region GetAllFileStatesAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAllFileStatesAsync_WhenEmpty_ReturnsEmptyList()
    {
        // Act
        var result = await _sut.GetAllFileStatesAsync();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAllFileStatesAsync_ReturnsAllTrackedFiles()
    {
        // Arrange
        var files = CreateTestFileMetadata(7);
        await SaveBackupState(files);

        // Act
        var result = await _sut.GetAllFileStatesAsync();

        // Assert
        result.Should().HaveCount(7);
        result.Should().OnlyContain(f => !string.IsNullOrEmpty(f.RelativePath));
        result.Should().OnlyContain(f => f.SizeBytes > 0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAllFileStatesAsync_AfterUpdate_ReturnsUpdatedStates()
    {
        // Arrange
        var initialFiles = CreateTestFileMetadata(3);
        await SaveBackupState(initialFiles);

        var runId = Guid.NewGuid();
        var updatedFiles = new List<FileMetadata>
        {
            initialFiles[0] with { Hash = "new-hash-1" },
            initialFiles[1] with { Hash = "new-hash-2" },
            CreateTestFileMetadata(1, startIndex: 10)[0] // New file
        };
        await _sut.SaveBackupSuccessAsync(runId, "new-commit", updatedFiles);

        // Act
        var result = await _sut.GetAllFileStatesAsync();

        // Assert
        result.Should().HaveCount(4); // 3 updated + 1 new (original file 2 still tracked)
    }

    #endregion

    #region RemoveDeletedFilesAsync Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RemoveDeletedFilesAsync_RemovesSpecifiedFiles()
    {
        // Arrange
        var files = CreateTestFileMetadata(5);
        await SaveBackupState(files);

        var filesToDelete = new List<string> { files[1].FilePath, files[3].FilePath };

        // Act
        await _sut.RemoveDeletedFilesAsync(filesToDelete);

        // Assert
        var remainingFiles = await _sut.GetAllFileStatesAsync();
        remainingFiles.Should().HaveCount(3);
        remainingFiles.Should().NotContain(f => f.RelativePath == files[1].FilePath);
        remainingFiles.Should().NotContain(f => f.RelativePath == files[3].FilePath);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RemoveDeletedFilesAsync_WithEmptyList_DoesNothing()
    {
        // Arrange
        var files = CreateTestFileMetadata(3);
        await SaveBackupState(files);

        // Act
        await _sut.RemoveDeletedFilesAsync(new List<string>());

        // Assert
        var remainingFiles = await _sut.GetAllFileStatesAsync();
        remainingFiles.Should().HaveCount(3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RemoveDeletedFilesAsync_WithNonExistentPaths_DoesNotThrow()
    {
        // Arrange
        var files = CreateTestFileMetadata(2);
        await SaveBackupState(files);

        var pathsToDelete = new List<string>
        {
            "/non/existent/file1.txt",
            "/non/existent/file2.txt"
        };

        // Act
        var act = async () => await _sut.RemoveDeletedFilesAsync(pathsToDelete);

        // Assert
        await act.Should().NotThrowAsync();
        
        var remainingFiles = await _sut.GetAllFileStatesAsync();
        remainingFiles.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RemoveDeletedFilesAsync_IsCaseInsensitive()
    {
        // Arrange
        var files = new List<FileMetadata>
        {
            new("/Path/To/MyFile.TXT", 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash1")
        };
        await SaveBackupState(files);

        // Act
        await _sut.RemoveDeletedFilesAsync(new List<string> { "/path/to/myfile.txt" });

        // Assert
        var remainingFiles = await _sut.GetAllFileStatesAsync();
        remainingFiles.Should().BeEmpty();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates test file metadata with predictable values.
    /// </summary>
    private static List<FileMetadata> CreateTestFileMetadata(int count, int startIndex = 0)
    {
        var files = new List<FileMetadata>();
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);

        for (var i = 0; i < count; i++)
        {
            var index = startIndex + i;
            files.Add(new FileMetadata(
                FilePath: $"/test/path/file{index}.txt",
                SizeBytes: 1000 + (index * 100),
                LastModified: baseTime.AddMinutes(index),
                Created: baseTime.AddMinutes(index),
                Hash: $"hash-{index:D4}"));
        }

        return files;
    }

    /// <summary>
    /// Helper to save a backup state for testing.
    /// </summary>
    private async Task SaveBackupState(List<FileMetadata> files)
    {
        var runId = Guid.NewGuid();
        var commitId = $"commit-{runId:N}";
        await _sut.SaveBackupSuccessAsync(runId, commitId, files);
    }

    #endregion
}
