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

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
