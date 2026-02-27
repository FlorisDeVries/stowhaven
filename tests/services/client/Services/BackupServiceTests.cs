using System.Runtime.CompilerServices;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Clients.BackupApi.DTOs;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FluentAssertions;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Unit tests for BackupService using mocked dependencies.
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly Mock<ILogger<BackupService>> _mockLogger = new();
    private readonly TelemetryProvider _telemetryProvider = new();
    private readonly Mock<IBackupApiClient> _mockApiClient = new();
    private readonly Mock<IBackupStateService> _mockStateService = new();
    private readonly Mock<IBackupScanner> _mockScanner = new();
    private readonly Mock<IFileUploader> _mockUploader = new();
    private readonly IOptions<BackupClientOptions> _options;
    private readonly BackupService _sut;
    private readonly string _testDirectory;

    public BackupServiceTests()
    {
        // Create a temporary directory for tests to avoid validation errors
        _testDirectory = Path.Combine(Path.GetTempPath(), $"backup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new Dictionary<string, string>
            {
                ["default"] = _testDirectory
            },
            MaxFailurePercentage = 10
        });

        _sut = new BackupService(
            _mockLogger.Object,
            _telemetryProvider,
            _mockApiClient.Object,
            _mockStateService.Object,
            _mockScanner.Object,
            _mockUploader.Object,
            _options);
    }

    // Helper method to create async enumerable from array
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
    public async Task Backup_WhenNoFiles_ShouldSucceedWithoutStartingRun()
    {
        // Arrange
        var deviceState = new DeviceState(Guid.NewGuid(), null, null, null, 0, 0);
        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync<TaggedFile>());

        _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.StartBackupRun(
            It.IsAny<StartBackupRunRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenNewFiles_ShouldStartRunAndUpload()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var deviceState = new DeviceState(deviceId, null, null, null, 0, 0);
        var runId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var file1 = new TaggedFile(
            "default",
            _testDirectory,
            new FileMetadata(
                Path.Combine(_testDirectory, "file1.txt"),
                100,
                now,
                now.AddDays(-1),
                "hash1"));

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(file1));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));

        _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockStateService.Setup(x => x.UpsertFileStateBatchAsync(
                It.IsAny<IReadOnlyList<(string, FileMetadata)>>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FileMetadata>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<StartBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => files);

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.StartBackupRun(
            It.Is<StartBackupRunRequest>(r => r.DeviceId == deviceId),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockApiClient.Verify(x => x.CommitBackupRun(
            It.Is<CommitBackupRunRequest>(r => r.RunId == runId),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockUploader.Verify(x => x.UploadFilesAsync(
            It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
            It.Is<IReadOnlyList<TaggedFile>>(files => files.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockStateService.Verify(x => x.UpsertFileStateBatchAsync(
            It.IsAny<IReadOnlyList<(string, FileMetadata)>>(),
            It.Is<Guid>(id => id == runId),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockStateService.Verify(x => x.SaveBackupSuccessAsync(
            It.Is<Guid>(id => id == runId),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<FileMetadata>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenTargetDoesNotExist_ShouldThrowException()
    {
        // Arrange
        var invalidOptions = Options.Create(new BackupClientOptions
        {
            BackupTargets = new Dictionary<string, string>
            {
                ["default"] = "/nonexistent/path/that/does/not/exist"
            },
            MaxFailurePercentage = 10
        });

        var sut = new BackupService(
            _mockLogger.Object,
            _telemetryProvider,
            _mockApiClient.Object,
            _mockStateService.Object,
            _mockScanner.Object,
            _mockUploader.Object,
            invalidOptions);

        var deviceState = new DeviceState(Guid.NewGuid(), null, null, null, 0, 0);
        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.Backup(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenCancelled_ShouldThrowOperationCancelledException()
    {
        // Arrange
        var deviceState = new DeviceState(Guid.NewGuid(), null, null, null, 0, 0);
        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        // Mock the scanner to use the async enumerable that checks cancellation
        static async IAsyncEnumerable<TaggedFile> ThrowOnCancellation([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            yield break;
        }

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyDictionary<string, string> _, string[]? __, CancellationToken ct) => ThrowOnCancellation(ct));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.Backup(cts.Token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenModifiedFiles_ShouldDetectAndUpload()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var deviceState = new DeviceState(deviceId, now.AddDays(-1), runId, "backup-run", 5, 1000);

        var modifiedFile = new TaggedFile(
            "default",
            _testDirectory,
            new FileMetadata(
                Path.Combine(_testDirectory, "modified.txt"),
                200,
                now,
                now.AddDays(-1),
                "hash-modified"));

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(modifiedFile));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.Modified));

        _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockStateService.Setup(x => x.UpsertFileStateBatchAsync(
                It.IsAny<IReadOnlyList<(string, FileMetadata)>>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FileMetadata>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var newRunId = Guid.NewGuid();
        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<StartBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = newRunId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => files);

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockUploader.Verify(x => x.UploadFilesAsync(
            It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
            It.Is<IReadOnlyList<TaggedFile>>(files => files.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenDeletedFilesOnly_ShouldNotStartBackupRun()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var lastRunId = Guid.NewGuid();
        var deviceState = new DeviceState(deviceId, DateTime.UtcNow.AddDays(-1), lastRunId, "backup-run", 5, 1000);

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync<TaggedFile>());

        var deletedFiles = new[] { "/path/to/deleted1.txt", "/path/to/deleted2.txt" };
        _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedFiles);

        _mockStateService.Setup(x => x.RemoveDeletedFilesAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.StartBackupRun(
            It.IsAny<StartBackupRunRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockStateService.Verify(x => x.RemoveDeletedFilesAsync(
            It.Is<IReadOnlyList<string>>(files => files.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenUnchangedFiles_ShouldNotUpload()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var deviceState = new DeviceState(deviceId, DateTime.UtcNow.AddDays(-1), Guid.NewGuid(), "backup-run", 5, 1000);

        var unchangedFile = new TaggedFile(
            "default",
            _testDirectory,
            new FileMetadata(
                Path.Combine(_testDirectory, "unchanged.txt"),
                100,
                DateTime.UtcNow,
                DateTime.UtcNow.AddDays(-2),
                "hash-unchanged"));

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(unchangedFile));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, false, FileChangeType.Unchanged));

        _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.StartBackupRun(
            It.IsAny<StartBackupRunRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockUploader.Verify(x => x.UploadFilesAsync(
            It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
            It.IsAny<IReadOnlyList<TaggedFile>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenPartialUploadFailure_ShouldSaveSuccessfulUploads()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var deviceState = new DeviceState(deviceId, null, null, null, 0, 0);
        var now = DateTime.UtcNow;

        // Create 11 files so that 1 failure = 9% failure rate (within 10% threshold)
        var files = Enumerable.Range(1, 11)
            .Select(i => new TaggedFile("default", _testDirectory,
                new FileMetadata(Path.Combine(_testDirectory, $"file{i}.txt"), 100, now, now.AddDays(-1), $"hash{i}")))
            .ToArray();

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(files));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));

        _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockStateService.Setup(x => x.UpsertFileStateBatchAsync(
                It.IsAny<IReadOnlyList<(string, FileMetadata)>>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FileMetadata>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<StartBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Simulate partial failure: 10 out of 11 files succeed (9% failure rate, within 10% threshold)
        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> fileList, CancellationToken _) => 
                fileList.Take(10).ToList());

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockStateService.Verify(x => x.UpsertFileStateBatchAsync(
            It.Is<IReadOnlyList<(string, FileMetadata)>>(f => f.Count == 10),
            It.Is<Guid>(id => id == runId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenFailureThresholdExceeded_ShouldThrowException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var deviceState = new DeviceState(deviceId, null, null, null, 0, 0);
        var now = DateTime.UtcNow;

        var files = Enumerable.Range(1, 20)
            .Select(i => new TaggedFile("default", _testDirectory,
                new FileMetadata(Path.Combine(_testDirectory, $"file{i}.txt"), 100, now, now.AddDays(-1), $"hash{i}")))
            .ToArray();

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(files));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));

        _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockStateService.Setup(x => x.UpsertFileStateBatchAsync(
                It.IsAny<IReadOnlyList<(string, FileMetadata)>>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<StartBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        // Simulate 50% failure rate (exceeds 10% threshold)
        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => 
                files.Take(files.Count / 2).ToList());

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.Backup(CancellationToken.None));
        exception.Message.Should().Contain("exceeding");
        exception.Message.Should().Contain("threshold");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenApiStartBackupRunFails_ShouldThrowException()
    {
        // Arrange
        var deviceState = new DeviceState(Guid.NewGuid(), null, null, null, 0, 0);
        var now = DateTime.UtcNow;

        var file1 = new TaggedFile("default", _testDirectory,
            new FileMetadata(Path.Combine(_testDirectory, "file1.txt"), 100, now, now.AddDays(-1), "hash1"));

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(file1));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<StartBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.Backup(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WithMultipleTargets_ShouldProcessAllTargets()
    {
        // Arrange
        var testDir2 = Path.Combine(Path.GetTempPath(), $"backup-test2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir2);

        try
        {
            var multiTargetOptions = Options.Create(new BackupClientOptions
            {
                BackupTargets = new Dictionary<string, string>
                {
                    ["target1"] = _testDirectory,
                    ["target2"] = testDir2
                },
                MaxFailurePercentage = 10
            });

            var sut = new BackupService(
                _mockLogger.Object,
                _telemetryProvider,
                _mockApiClient.Object,
                _mockStateService.Object,
                _mockScanner.Object,
                _mockUploader.Object,
                multiTargetOptions);

            var deviceId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var deviceState = new DeviceState(deviceId, null, null, null, 0, 0);
            var now = DateTime.UtcNow;

            var file1 = new TaggedFile("target1", _testDirectory,
                new FileMetadata(Path.Combine(_testDirectory, "file1.txt"), 100, now, now.AddDays(-1), "hash1"));
            var file2 = new TaggedFile("target2", testDir2,
                new FileMetadata(Path.Combine(testDir2, "file2.txt"), 200, now, now.AddDays(-1), "hash2"));

            _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(deviceState);

            _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                    It.IsAny<IReadOnlyDictionary<string, string>>(),
                    It.IsAny<string[]?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(ToAsync(file1, file2));

            _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));

            _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                    It.IsAny<HashSet<string>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<string>());

            _mockStateService.Setup(x => x.UpsertFileStateBatchAsync(
                    It.IsAny<IReadOnlyList<(string, FileMetadata)>>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<FileMetadata>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<StartBackupRunRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StartBackupRunResponse 
                { 
                    DeviceId = deviceId,
                    RunId = runId,
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = BackupRunStatus.Processing,
                    SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
                });

            _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _mockUploader.Setup(x => x.UploadFilesAsync(
                    It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                    It.IsAny<IReadOnlyList<TaggedFile>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => files);

            // Act
            var result = await sut.Backup(CancellationToken.None);

            // Assert
            result.Should().BeTrue();
            _mockScanner.Verify(x => x.ScanAllTargetsAsync(
                It.Is<IReadOnlyDictionary<string, string>>(t => t.Count == 2 && t.ContainsKey("target1") && t.ContainsKey("target2")),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(testDir2))
            {
                Directory.Delete(testDir2, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenMixedChangeTypes_ShouldOnlyUploadChangedFiles()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var deviceState = new DeviceState(deviceId, DateTime.UtcNow.AddDays(-1), Guid.NewGuid(), "backup-run", 2, 200);
        var now = DateTime.UtcNow;

        var newFile = new TaggedFile("default", _testDirectory,
            new FileMetadata(Path.Combine(_testDirectory, "new.txt"), 100, now, now.AddDays(-1), "hash1"));
        var modifiedFile = new TaggedFile("default", _testDirectory,
            new FileMetadata(Path.Combine(_testDirectory, "modified.txt"), 150, now, now.AddDays(-1), "hash2"));
        var unchangedFile = new TaggedFile("default", _testDirectory,
            new FileMetadata(Path.Combine(_testDirectory, "unchanged.txt"), 200, now.AddDays(-5), now.AddDays(-10), "hash3"));

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(newFile, modifiedFile, unchangedFile));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.Is<TaggedFile>(f => f.Metadata.Hash == "hash1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.Is<TaggedFile>(f => f.Metadata.Hash == "hash2"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.Modified));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.Is<TaggedFile>(f => f.Metadata.Hash == "hash3"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, false, FileChangeType.Unchanged));

        _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockStateService.Setup(x => x.UpsertFileStateBatchAsync(
                It.IsAny<IReadOnlyList<(string, FileMetadata)>>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FileMetadata>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<StartBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => files);

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockUploader.Verify(x => x.UploadFilesAsync(
            It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
            It.Is<IReadOnlyList<TaggedFile>>(files => files.Count == 2), // Only new and modified
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenCommitFails_ShouldThrowException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var deviceState = new DeviceState(deviceId, null, null, null, 0, 0);
        var now = DateTime.UtcNow;

        var file1 = new TaggedFile("default", _testDirectory,
            new FileMetadata(Path.Combine(_testDirectory, "file1.txt"), 100, now, now.AddDays(-1), "hash1"));

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(file1));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));

        _mockScanner.Setup(x => x.DetectDeletedFilesAsync(
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockStateService.Setup(x => x.UpsertFileStateBatchAsync(
                It.IsAny<IReadOnlyList<(string, FileMetadata)>>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<StartBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Commit failed"));

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => files);

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.Backup(CancellationToken.None));
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
