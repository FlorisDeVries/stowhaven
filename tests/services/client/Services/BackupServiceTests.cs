using System.Runtime.CompilerServices;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Telemetry;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.State;
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

        _mockApiClient
            .Setup(x => x.RegisterDevice(It.IsAny<RegisterDeviceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RegisterDeviceRequest req, CancellationToken ct) => new DeviceRegistrationResponse
            {
                DeviceId = req.DeviceId ?? Guid.NewGuid(),
                TenantId = "test-tenant",
                UserId = "test-user",
                DisplayName = req.DisplayName,
                Status = DeviceRegistrationStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });

        _mockApiClient
            .Setup(x => x.CommitBackupRun(It.IsAny<Guid>(), It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid deviceId, CommitBackupRunRequest req, CancellationToken ct) => new CommitBackupRunResponse
            {
                CommitId = Guid.NewGuid(),
                DeviceId = deviceId,
                RunId = req.RunId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            });

        _mockApiClient
            .Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid deviceId, Guid commitId, CancellationToken ct) => new CommitStatusResponse
            {
                DeviceId = deviceId,
                CommitId = commitId,
                Status = CommitJobStatus.Succeeded,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            });
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

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.StartBackupRun(
            It.IsAny<Guid>(),
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

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FileMetadata>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<Guid>(), It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid committedDeviceId, CommitBackupRunRequest req, CancellationToken ct) => new CommitBackupRunResponse
            {
                CommitId = Guid.NewGuid(),
                DeviceId = committedDeviceId,
                RunId = req.RunId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            });

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => new UploadBatchResult(files, [], 0));

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.StartBackupRun(
            It.Is<Guid>(id => id == deviceId),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockApiClient.Verify(x => x.CommitBackupRun(
            It.Is<Guid>(id => id == deviceId),
            It.Is<CommitBackupRunRequest>(r => r.RunId == runId),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockUploader.Verify(x => x.UploadFilesAsync(
            It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
            It.Is<IReadOnlyList<TaggedFile>>(files => files.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockStateService.Verify(x => x.AppendPendingRunFilesAsync(
            It.IsAny<Guid>(),
            It.Is<Guid>(id => id == runId),
            It.Is<IReadOnlyList<TaggedFile>>(files => files.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockStateService.Verify(x => x.PromotePendingRunFilesToStateAsync(
            It.IsAny<Guid>(),
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
    public async Task Backup_WhenFileChangedSinceScan_ShouldSkipItAndUploadTheRest()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var deviceState = new DeviceState(deviceId, null, null, null, 0, 0);
        var runId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // A normal file whose synthetic path does not exist on disk is left alone (uploaded as usual).
        var stableFile = new TaggedFile("default", _testDirectory,
            new FileMetadata(Path.Combine(_testDirectory, "stable.txt"), 100, now, now.AddDays(-1), "hash-stable"));

        // A file that exists on disk but whose size no longer matches what was scanned (it changed
        // between scan and upload) must be skipped so its stale manifest entry is never committed.
        var volatilePath = Path.Combine(_testDirectory, "volatile.txt");
        await File.WriteAllTextAsync(volatilePath, "12345"); // 5 bytes on disk
        var changedFile = new TaggedFile("default", _testDirectory,
            new FileMetadata(volatilePath, 100, now, now.AddDays(-1), "hash-volatile")); // scanned as 100 bytes

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);

        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync(stableFile, changedFile));

        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<FileMetadata>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse
            {
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => new UploadBatchResult(files, [], 0));

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert - backup still succeeds, and the changed file was excluded from the upload batch.
        result.Should().BeTrue();
        _mockUploader.Verify(x => x.UploadFilesAsync(
            It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
            It.Is<IReadOnlyList<TaggedFile>>(files =>
                files.Count == 1 && files.All(f => f.Metadata.FilePath != volatilePath)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenCommitStillProcessing_ExitsCleanlyAndKeepsPendingRun()
    {
        // Arrange - zero wait so the commit-status poll "times out" immediately while still Processing.
        var deviceId = Guid.NewGuid();
        var deviceState = new DeviceState(deviceId, null, null, null, 0, 0);
        var runId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new Dictionary<string, string> { ["default"] = _testDirectory },
            MaxFailurePercentage = 10,
            CommitStatusTimeoutSeconds = 0,
            CommitStatusPollIntervalSeconds = 1
        });
        var sut = new BackupService(
            _mockLogger.Object, _telemetryProvider, _mockApiClient.Object,
            _mockStateService.Object, _mockScanner.Object, _mockUploader.Object, options);

        var file1 = new TaggedFile("default", _testDirectory,
            new FileMetadata(Path.Combine(_testDirectory, "file1.txt"), 100, now, now.AddDays(-1), "hash1"));

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);
        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsync(file1));
        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));
        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse
            {
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });
        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(), It.IsAny<IReadOnlyList<TaggedFile>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => new UploadBatchResult(files, [], 0));

        // The commit never reaches a terminal state during the (zero-length) wait.
        _mockApiClient.Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid d, Guid c, CancellationToken _) => new CommitStatusResponse
            {
                DeviceId = d,
                CommitId = c,
                Status = CommitJobStatus.Processing,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

        // Act
        var result = await sut.Backup(CancellationToken.None);

        // Assert - clean, non-fatal exit; the run is NOT finalized locally so a later run can resume it.
        result.Should().BeTrue();
        _mockStateService.Verify(x => x.ClearPendingBackupRunAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockStateService.Verify(x => x.SaveBackupSuccessAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<FileMetadata>>(), It.IsAny<CancellationToken>()), Times.Never);
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

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FileMetadata>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var newRunId = Guid.NewGuid();
        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = newRunId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<Guid>(), It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid committedDeviceId, CommitBackupRunRequest req, CancellationToken ct) => new CommitBackupRunResponse
            {
                CommitId = Guid.NewGuid(),
                DeviceId = committedDeviceId,
                RunId = req.RunId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            });

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => new UploadBatchResult(files, [], 0));

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
    public async Task Backup_WhenNoDeletionsDetected_ShouldStillRederiveRunDeletions()
    {
        // Arrange: an interrupted earlier attempt at this run may have journaled deletions for files
        // that exist again. Re-deriving must happen even when this scan finds nothing deleted,
        // otherwise those stale entries reach the manifest and drop live files from tracked state.
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        ArrangeSingleFileBackup(deviceId, runId, sasExpiresIn: TimeSpan.FromMinutes(60));

        _mockStateService.Setup(x => x.CountScanDeletionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => new UploadBatchResult(files, [], 0));

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockStateService.Verify(x => x.RecordScanDeletionsAsync(
            It.Is<Guid>(id => id == deviceId),
            It.Is<Guid>(id => id == runId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenDeletedFilesOnly_ShouldStartRunAndCommitDeletionManifest()
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

        // Deletions are detected inside the state store and never surfaced as a list.
        _mockStateService.Setup(x => x.CountScanDeletionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        _mockStateService.Setup(x => x.RecordScanDeletionsAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FileMetadata>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse
            {
                DeviceId = deviceId,
                RunId = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.StartBackupRun(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockApiClient.Verify(x => x.CommitBackupRun(
            It.Is<Guid>(id => id == deviceId),
            It.IsAny<CommitBackupRunRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockStateService.Verify(x => x.RecordScanDeletionsAsync(
            It.Is<Guid>(id => id == deviceId),
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockStateService.Verify(x => x.ApplyPendingRunDeletionsAsync(
            It.Is<Guid>(id => id == deviceId),
            It.IsAny<Guid>(),
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

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.StartBackupRun(
            It.IsAny<Guid>(),
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

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FileMetadata>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<Guid>(), It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid committedDeviceId, CommitBackupRunRequest req, CancellationToken ct) => new CommitBackupRunResponse
            {
                CommitId = Guid.NewGuid(),
                DeviceId = committedDeviceId,
                RunId = req.RunId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            });

        // Simulate partial failure: 10 out of 11 files succeed (9% failure rate, within 10% threshold)
        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> fileList, CancellationToken _) =>
            {
                var uploaded = fileList.Take(10).ToList();
                return new UploadBatchResult(uploaded, [], fileList.Count - uploaded.Count);
            });

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert: only the files that actually uploaded reach the journal, and the journal is what
        // becomes tracked state at commit time.
        result.Should().BeTrue();
        _mockStateService.Verify(x => x.AppendPendingRunFilesAsync(
            It.IsAny<Guid>(),
            It.Is<Guid>(id => id == runId),
            It.Is<IReadOnlyList<TaggedFile>>(f => f.Count == 10),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockStateService.Verify(x => x.PromotePendingRunFilesToStateAsync(
            It.IsAny<Guid>(),
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

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
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
            {
                var uploaded = files.Take(files.Count / 2).ToList();
                return new UploadBatchResult(uploaded, [], files.Count - uploaded.Count);
            });

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

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
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

            _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<FileMetadata>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StartBackupRunResponse 
                { 
                    DeviceId = deviceId,
                    RunId = runId,
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = BackupRunStatus.Processing,
                    SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
                });

            _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<Guid>(), It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid committedDeviceId, CommitBackupRunRequest req, CancellationToken ct) => new CommitBackupRunResponse
                {
                    CommitId = Guid.NewGuid(),
                    DeviceId = committedDeviceId,
                    RunId = req.RunId,
                    Status = CommitJobStatus.Queued,
                    CreatedAt = DateTimeOffset.UtcNow
                });

            _mockUploader.Setup(x => x.UploadFilesAsync(
                    It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                    It.IsAny<IReadOnlyList<TaggedFile>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => new UploadBatchResult(files, [], 0));

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

        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<FileMetadata>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<Guid>(), It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid committedDeviceId, CommitBackupRunRequest req, CancellationToken ct) => new CommitBackupRunResponse
            {
                CommitId = Guid.NewGuid(),
                DeviceId = committedDeviceId,
                RunId = req.RunId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            });

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => new UploadBatchResult(files, [], 0));

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

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse 
            { 
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.CommitBackupRun(It.IsAny<Guid>(), It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Commit failed"));

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => new UploadBatchResult(files, [], 0));

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.Backup(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenSasNearExpiry_ShouldRefreshBeforeUploading()
    {
        // Arrange: a run whose SAS is already within the safety window, so it must be refreshed
        // before the batch is uploaded rather than allowed to expire mid-upload.
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        ArrangeSingleFileBackup(deviceId, runId, sasExpiresIn: TimeSpan.FromSeconds(30));

        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> files, CancellationToken _) => new UploadBatchResult(files, [], 0));

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.RefreshBackupRunSas(
            It.Is<Guid>(id => id == deviceId),
            It.Is<Guid>(id => id == runId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenSasExpiresMidBatch_ShouldRefreshAndRetryAffectedFiles()
    {
        // Arrange: SAS starts with a full lifetime (no proactive refresh), but the first upload
        // attempt reports the file as SAS-expired. The service must refresh and retry that file.
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        ArrangeSingleFileBackup(deviceId, runId, sasExpiresIn: TimeSpan.FromMinutes(60));

        // First attempt reports every file as SAS-expired; the retry (after refresh) uploads them.
        // Echo the actual staged files so their generated UniqueFileId flows into the manifest.
        var uploadCall = 0;
        _mockUploader.Setup(x => x.UploadFilesAsync(
                It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
                It.IsAny<IReadOnlyList<TaggedFile>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Azure.Storage.Blobs.BlobContainerClient _, IReadOnlyList<TaggedFile> f, CancellationToken _) =>
                Interlocked.Increment(ref uploadCall) == 1
                    ? new UploadBatchResult([], f, 0)
                    : new UploadBatchResult(f, [], 0));

        // Act
        var result = await _sut.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mockApiClient.Verify(x => x.RefreshBackupRunSas(
            It.Is<Guid>(id => id == deviceId),
            It.Is<Guid>(id => id == runId),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockUploader.Verify(x => x.UploadFilesAsync(
            It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(),
            It.IsAny<IReadOnlyList<TaggedFile>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        _mockStateService.Verify(x => x.SaveBackupSuccessAsync(
            It.Is<Guid>(id => id == runId),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<FileMetadata>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenPendingCommitFinished_ContinuesWithCurrentFilesystemScan()
    {
        var deviceId = Guid.NewGuid();
        var pendingRunId = Guid.NewGuid();
        var commitId = Guid.NewGuid();
        var initialState = new DeviceState(deviceId, null, null, null, 0, 0);
        var finalizedState = new DeviceState(
            deviceId,
            DateTimeOffset.UtcNow,
            pendingRunId,
            commitId.ToString("N"),
            2_000_000,
            123_456);

        _mockStateService.SetupSequence(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(initialState)
            .ReturnsAsync(finalizedState);
        _mockStateService.Setup(x => x.GetPendingBackupRunAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePendingRun(deviceId, pendingRunId, commitId));
        _mockApiClient.Setup(x => x.GetCommitStatus(deviceId, commitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitStatusResponse
            {
                DeviceId = deviceId,
                RunId = pendingRunId,
                CommitId = commitId,
                Status = CommitJobStatus.Succeeded,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            });
        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync<TaggedFile>());

        var result = await _sut.Backup(CancellationToken.None);

        result.Should().BeTrue();
        _mockStateService.Verify(
            x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _mockScanner.Verify(x => x.ScanAllTargetsAsync(
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string[]?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenPendingCommitStillProcessing_DoesNotStartAnotherScan()
    {
        var deviceId = Guid.NewGuid();
        var pendingRunId = Guid.NewGuid();
        var commitId = Guid.NewGuid();
        var options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new Dictionary<string, string> { ["default"] = _testDirectory },
            MaxFailurePercentage = 10,
            CommitStatusTimeoutSeconds = 0,
            CommitStatusPollIntervalSeconds = 1
        });
        var sut = new BackupService(
            _mockLogger.Object,
            _telemetryProvider,
            _mockApiClient.Object,
            _mockStateService.Object,
            _mockScanner.Object,
            _mockUploader.Object,
            options);

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(deviceId, null, null, null, 0, 0));
        _mockStateService.Setup(x => x.GetPendingBackupRunAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePendingRun(deviceId, pendingRunId, commitId));
        _mockApiClient.Setup(x => x.GetCommitStatus(deviceId, commitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitStatusResponse
            {
                DeviceId = deviceId,
                RunId = pendingRunId,
                CommitId = commitId,
                Status = CommitJobStatus.Processing,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

        var result = await sut.Backup(CancellationToken.None);

        result.Should().BeTrue();
        _mockScanner.Verify(x => x.ScanAllTargetsAsync(
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string[]?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenServerSkippedFiles_RemovesThemBeforePromotingJournal()
    {
        var deviceId = Guid.NewGuid();
        var pendingRunId = Guid.NewGuid();
        var commitId = Guid.NewGuid();
        var state = new DeviceState(deviceId, null, null, null, 0, 0);

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);
        _mockStateService.Setup(x => x.GetPendingBackupRunAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePendingRun(deviceId, pendingRunId, commitId));
        _mockApiClient.Setup(x => x.GetCommitStatus(deviceId, commitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitStatusResponse
            {
                DeviceId = deviceId,
                RunId = pendingRunId,
                CommitId = commitId,
                Status = CommitJobStatus.CompletedWithErrors,
                FilesFailed = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            });
        _mockApiClient.Setup(x => x.ListFailedCommitFiles(
                deviceId, commitId, 500, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListCommitFileProgressResponse
            {
                DeviceId = deviceId,
                RunId = pendingRunId,
                CommitId = commitId,
                Files =
                [
                    CreateCommitFileProgress(deviceId, pendingRunId, commitId, "data/a.txt"),
                    CreateCommitFileProgress(deviceId, pendingRunId, commitId, "projects/b.txt")
                ],
                PageSize = 500
            });
        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync<TaggedFile>());

        var result = await _sut.Backup(CancellationToken.None);

        result.Should().BeTrue();
        _mockStateService.Verify(x => x.RemovePendingRunFilesAsync(
            deviceId,
            pendingRunId,
            It.Is<IReadOnlyList<string>>(paths =>
                paths.SequenceEqual(new[] { "data/a.txt", "projects/b.txt" })),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockStateService.Verify(x => x.PromotePendingRunFilesToStateAsync(
            deviceId, pendingRunId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenFailedFilePageIsIncomplete_PreservesPendingJournal()
    {
        var deviceId = Guid.NewGuid();
        var pendingRunId = Guid.NewGuid();
        var commitId = Guid.NewGuid();
        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(deviceId, null, null, null, 0, 0));
        _mockStateService.Setup(x => x.GetPendingBackupRunAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePendingRun(deviceId, pendingRunId, commitId));
        _mockApiClient.Setup(x => x.GetCommitStatus(deviceId, commitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitStatusResponse
            {
                DeviceId = deviceId,
                RunId = pendingRunId,
                CommitId = commitId,
                Status = CommitJobStatus.CompletedWithErrors,
                FilesFailed = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            });
        _mockApiClient.Setup(x => x.ListFailedCommitFiles(
                deviceId, commitId, 500, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListCommitFileProgressResponse
            {
                DeviceId = deviceId,
                RunId = pendingRunId,
                CommitId = commitId,
                Files = [CreateCommitFileProgress(deviceId, pendingRunId, commitId, "data/only-one.txt")],
                PageSize = 500
            });

        var act = () => _sut.Backup(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reported 2 failed file(s)*returned 1*");
        _mockStateService.Verify(x => x.PromotePendingRunFilesToStateAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockStateService.Verify(x => x.ClearPendingBackupRunAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Backup_WhenOlderClientPromotedRejectedFiles_RemovesThemBeforeScan()
    {
        var deviceId = Guid.NewGuid();
        var lastRunId = Guid.NewGuid();
        var lastCommitId = Guid.NewGuid();
        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(
                deviceId,
                DateTimeOffset.UtcNow.AddHours(-1),
                lastRunId,
                lastCommitId.ToString("N"),
                100,
                1_000));
        _mockApiClient.Setup(x => x.GetCommitStatus(deviceId, lastCommitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitStatusResponse
            {
                DeviceId = deviceId,
                RunId = lastRunId,
                CommitId = lastCommitId,
                Status = CommitJobStatus.CompletedWithErrors,
                FilesFailed = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            });
        _mockApiClient.Setup(x => x.ListFailedCommitFiles(
                deviceId, lastCommitId, 500, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListCommitFileProgressResponse
            {
                DeviceId = deviceId,
                RunId = lastRunId,
                CommitId = lastCommitId,
                Files = [CreateCommitFileProgress(deviceId, lastRunId, lastCommitId, "projects/rejected.txt")],
                PageSize = 500
            });
        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsync<TaggedFile>());

        var result = await _sut.Backup(CancellationToken.None);

        result.Should().BeTrue();
        _mockStateService.Verify(x => x.RemoveTrackedFilesAsync(
            It.Is<IReadOnlyList<string>>(paths => paths.SequenceEqual(new[] { "projects/rejected.txt" })),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockScanner.Verify(x => x.ScanAllTargetsAsync(
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string[]?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static PendingBackupRun CreatePendingRun(Guid deviceId, Guid runId, Guid commitId)
    {
        var now = DateTimeOffset.UtcNow;
        return new PendingBackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = now.AddHours(-1),
            UploadSasUrlInfo = new SasUrlInfo
            {
                Url = new Uri("https://test.blob.core.windows.net/backups?sas=upload"),
                ExpiresAt = now.AddMinutes(30),
                TtlMinutes = 60
            },
            ManifestSasUrlInfo = new SasUrlInfo
            {
                Url = new Uri("https://test.blob.core.windows.net/backups?sas=manifest"),
                ExpiresAt = now.AddMinutes(30),
                TtlMinutes = 60
            },
            ManifestUploaded = true,
            CommitId = commitId
        };
    }

    private static CommitFileProgressResponse CreateCommitFileProgress(
        Guid deviceId,
        Guid runId,
        Guid commitId,
        string logicalPath)
        => new()
        {
            DeviceId = deviceId,
            RunId = runId,
            CommitId = commitId,
            UniqueFileId = $"uid-{logicalPath}",
            LogicalPath = logicalPath,
            Status = CommitFileStatus.Failed,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>
    /// Wires up scanner/state/api mocks for a backup of a single new file, returning that file.
    /// The run's SAS expiry is set to <paramref name="sasExpiresIn"/> from now.
    /// </summary>
    private TaggedFile ArrangeSingleFileBackup(Guid deviceId, Guid runId, TimeSpan sasExpiresIn)
    {
        var deviceState = new DeviceState(deviceId, null, null, null, 0, 0);
        var now = DateTime.UtcNow;
        var file = new TaggedFile(
            "default",
            _testDirectory,
            new FileMetadata(Path.Combine(_testDirectory, "file1.txt"), 100, now, now.AddDays(-1), "hash1"));

        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceState);
        _mockScanner.Setup(x => x.ScanAllTargetsAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsync(file));
        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, true, FileChangeType.New));
        _mockStateService.Setup(x => x.SaveBackupSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<FileMetadata>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockApiClient.Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartBackupRunResponse
            {
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Processing,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=token"), ExpiresAt = DateTimeOffset.UtcNow.Add(sasExpiresIn), TtlMinutes = 60 }
            });

        _mockApiClient.Setup(x => x.RefreshBackupRunSas(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid d, Guid r, CancellationToken _) => new RefreshSasUrlResponse
            {
                DeviceId = d,
                RunId = r,
                SasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=refreshed"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 },
                ManifestSasUrlInfo = new SasUrlInfo { Url = new Uri("https://test.blob.core.windows.net/backups?sas=refreshed-manifest"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60), TtlMinutes = 60 }
            });

        return file;
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
