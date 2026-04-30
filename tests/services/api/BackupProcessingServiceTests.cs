using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.DataLake;
using FluentAssertions;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Events;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.BackupWorker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using System.Text.Json;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Tests for BackupProcessingService to verify manifest processing, file operations, and state management.
/// </summary>
public class BackupProcessingServiceTests
{
    private readonly Mock<ILogger<BackupProcessingService>> _loggerMock;
    private readonly Mock<IBlobStorageService> _blobStorageServiceMock;
    private readonly Mock<IManifestManager> _manifestManagerMock;
    private readonly Mock<TelemetryProvider> _telemetryMock;
    private readonly Mock<BlobContainerClient> _containerClientMock;
    private readonly Dictionary<string, Mock<BlobClient>> _blobClients;
    private readonly BackupProcessingService _sut;

    public BackupProcessingServiceTests()
    {
        _loggerMock = new Mock<ILogger<BackupProcessingService>>();
        _blobStorageServiceMock = new Mock<IBlobStorageService>();
        _manifestManagerMock = new Mock<IManifestManager>();
        _telemetryMock = new Mock<TelemetryProvider>();
        _containerClientMock = new Mock<BlobContainerClient>();
        _blobClients = new Dictionary<string, Mock<BlobClient>>();

        _sut = new BackupProcessingService(
            _loggerMock.Object,
            _blobStorageServiceMock.Object,
            _manifestManagerMock.Object,
            new ConfigurationBuilder().Build(),
            _telemetryMock.Object);

        // Default setup
        _blobStorageServiceMock
            .Setup(x => x.GetContainerClientAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_containerClientMock.Object);

        _blobStorageServiceMock
            .Setup(x => x.GetContainerNameAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("backups");

        _blobStorageServiceMock
            .Setup(x => x.IsUsingAzuriteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // Default to Azurite for simpler tests

        // Mock MoveBlobAsync to succeed by default
        _blobStorageServiceMock
            .Setup(x => x.MoveBlobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Setup GetBlobClient to return tracked blob clients (for manifest download)
        _containerClientMock
            .Setup(x => x.GetBlobClient(It.IsAny<string>()))
            .Returns<string>(blobName =>
            {
                if (!_blobClients.ContainsKey(blobName))
                {
                    _blobClients[blobName] = new Mock<BlobClient>();
                }
                return _blobClients[blobName].Object;
            });
    }

    #region Idempotency Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_WhenAlreadySucceeded_SkipsProcessing()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);

        var commitJob = new CommitJob
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            Status = CommitJobStatus.Succeeded, // Already processed
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        _manifestManagerMock
            .Setup(x => x.TryClaimCommitJobAsync(commitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, commitJob));

        // Act
        await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert - should not update commit job or process files
        _manifestManagerMock.Verify(
            x => x.UpdateCommitJobAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _manifestManagerMock.Verify(
            x => x.GetBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_WhenConcurrentProcessing_SkipsProcessing()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);

        var commitJob = new CommitJob
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            Status = CommitJobStatus.Processing, // Currently being processed
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        _manifestManagerMock
            .Setup(x => x.TryClaimCommitJobAsync(commitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, commitJob));

        // Act
        await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert - should not update anything
        _manifestManagerMock.Verify(
            x => x.UpdateCommitJobAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region Manifest Download Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_WithMissingManifest_ThrowsException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);

        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        // Setup manifest blob to return 404
        var manifestBlobClient = new Mock<BlobClient>();
        _containerClientMock
            .Setup(x => x.GetBlobClient(It.IsAny<string>()))
            .Returns(manifestBlobClient.Object);

        manifestBlobClient
            .Setup(x => x.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not Found"));

        // Act
        var act = async () => await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*manifest not found*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_IgnoresEventManifestPath_AndUsesDerivedPath()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var backupEvent = new BackupRunCommittedEvent
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CommittedAt = DateTimeOffset.UtcNow,
            StagingPath = $"staging/{deviceId:N}/{runId:N}/",
            ManifestPath = "runs/other-device/other-run/malicious-manifest.json"
        };

        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry>(), new List<string>());
        var expectedPath = $"runs/{deviceId:N}/{runId:N}/run-manifest.json";

        string? capturedPath = null;
        SetupManifestDownload(manifest, path =>
        {
            capturedPath = path;
        });

        // Act
        await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert
        capturedPath.Should().Be(expectedPath);
    }

    #endregion

    #region File Entry Processing Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_ProcessesNewFiles_Successfully()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var fileEntry = new ManifestFileEntry
        {
            RelativePath = "documents/test.txt",
            UniqueFileId = "abc123_20260419_xyz",
            Sha256 = "abc123",
            Size = 1024,
            Mtime = DateTimeOffset.UtcNow
        };

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry> { fileEntry }, new List<string>());
        SetupManifestDownload(manifest);

        // No existing file entry
        _manifestManagerMock
            .Setup(x => x.GetFileEntryAsync(deviceId, fileEntry.RelativePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileEntry?)null);

        // Act
        await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert - should save new FileVersion and FileEntry
        _manifestManagerMock.Verify(
            x => x.SaveFileVersionAsync(
                It.Is<FileVersion>(fv =>
                    fv.UniqueFileId == fileEntry.UniqueFileId &&
                    fv.State == FileVersionState.Active),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _manifestManagerMock.Verify(
            x => x.SaveFileEntryAsync(
                It.Is<FileEntry>(fe =>
                    fe.RelativePath == fileEntry.RelativePath &&
                    fe.CurrentVersionId == fileEntry.UniqueFileId &&
                    !fe.IsDeleted),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_WithChangedFile_RetiresOldVersion()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var oldFileId = "old123_20260418_abc";
        var newFileId = "new456_20260419_xyz";

        var fileEntry = new ManifestFileEntry
        {
            RelativePath = "documents/test.txt",
            UniqueFileId = newFileId,
            Sha256 = "new456",
            Size = 2048,
            Mtime = DateTimeOffset.UtcNow
        };

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry> { fileEntry }, new List<string>());
        SetupManifestDownload(manifest);

        // Setup existing file entry
        var existingFile = new FileEntry
        {
            DeviceId = deviceId,
            RelativePath = fileEntry.RelativePath,
            CurrentVersionId = oldFileId,
            Size = 1024,
            LastWriteUtc = DateTimeOffset.UtcNow.AddDays(-1),
            LastBackupRunId = Guid.NewGuid().ToString("N"),
            IsDeleted = false
        };

        _manifestManagerMock
            .Setup(x => x.GetFileEntryAsync(deviceId, fileEntry.RelativePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingFile);

        // Setup old file version
        var oldFileVersion = new FileVersion
        {
            DeviceId = deviceId,
            UniqueFileId = oldFileId,
            RelativePath = fileEntry.RelativePath,
            Sha256 = "old123",
            Size = 1024,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            State = FileVersionState.Active
        };

        _manifestManagerMock
            .Setup(x => x.GetFileVersionAsync(deviceId, oldFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldFileVersion);

        // Act
        await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert - should retire old version
        _manifestManagerMock.Verify(
            x => x.SaveFileVersionAsync(
                It.Is<FileVersion>(fv =>
                    fv.UniqueFileId == oldFileId &&
                    fv.State == FileVersionState.Retired &&
                    fv.RetiredAt != null),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Should save new version as Active
        _manifestManagerMock.Verify(
            x => x.SaveFileVersionAsync(
                It.Is<FileVersion>(fv =>
                    fv.UniqueFileId == newFileId &&
                    fv.State == FileVersionState.Active),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_WhenStagedBlobMissing_ThrowsAndDoesNotMoveBlob()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();
        var fileEntry = CreateFileEntry("documents/test.txt", "abc123_20260419_xyz");

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry> { fileEntry }, new List<string>());
        SetupManifestDownload(manifest);

        var sourcePath = $"staging/{deviceId:N}/{runId:N}/{fileEntry.UniqueFileId}";
        _blobClients[sourcePath]
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not Found"));

        // Act
        var act = async () => await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Staged blob not found*");

        _blobStorageServiceMock.Verify(
            x => x.MoveBlobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_WhenStagedBlobSizeDiffersFromManifest_ThrowsAndDoesNotMoveBlob()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();
        var fileEntry = CreateFileEntry("documents/test.txt", "abc123_20260419_xyz") with { Size = 1024 };

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry> { fileEntry }, new List<string>());
        SetupManifestDownload(manifest);
        SetupStagedBlobProperties(deviceId.ToString("N"), runId.ToString("N"), fileEntry, contentLength: 1000);

        // Act
        var act = async () => await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*size mismatch*");

        _blobStorageServiceMock.Verify(
            x => x.MoveBlobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_WhenStagedBlobHashMetadataMissing_ThrowsAndDoesNotMoveBlob()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();
        var fileEntry = CreateFileEntry("documents/test.txt", "abc123_20260419_xyz");

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry> { fileEntry }, new List<string>());
        SetupManifestDownload(manifest);
        SetupStagedBlobProperties(deviceId.ToString("N"), runId.ToString("N"), fileEntry, includeSha256Metadata: false);

        // Act
        var act = async () => await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{BackupBlobMetadata.Sha256}*");

        _blobStorageServiceMock.Verify(
            x => x.MoveBlobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_WhenStagedBlobHashMetadataDiffersFromManifest_ThrowsAndDoesNotMoveBlob()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();
        var fileEntry = CreateFileEntry("documents/test.txt", "abc123_20260419_xyz");

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry> { fileEntry }, new List<string>());
        SetupManifestDownload(manifest);
        SetupStagedBlobProperties(deviceId.ToString("N"), runId.ToString("N"), fileEntry, sha256: "different-hash");

        // Act
        var act = async () => await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SHA-256 metadata mismatch*");

        _blobStorageServiceMock.Verify(
            x => x.MoveBlobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region File Deletion Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_ProcessesDeletedFiles_Successfully()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var deletedPath = "documents/deleted.txt";
        var fileId = "abc123_20260418_xyz";

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry>(), new List<string> { deletedPath });
        SetupManifestDownload(manifest);

        // Setup existing file
        var existingFile = new FileEntry
        {
            DeviceId = deviceId,
            RelativePath = deletedPath,
            CurrentVersionId = fileId,
            Size = 1024,
            LastWriteUtc = DateTimeOffset.UtcNow.AddDays(-1),
            LastBackupRunId = Guid.NewGuid().ToString("N"),
            IsDeleted = false
        };

        _manifestManagerMock
            .Setup(x => x.GetFileEntryAsync(deviceId, deletedPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingFile);

        var fileVersion = new FileVersion
        {
            DeviceId = deviceId,
            UniqueFileId = fileId,
            RelativePath = deletedPath,
            Sha256 = "abc123",
            Size = 1024,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            State = FileVersionState.Active
        };

        _manifestManagerMock
            .Setup(x => x.GetFileVersionAsync(deviceId, fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fileVersion);

        // Act
        await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert - should mark FileEntry as deleted
        _manifestManagerMock.Verify(
            x => x.SaveFileEntryAsync(
                It.Is<FileEntry>(fe =>
                    fe.RelativePath == deletedPath &&
                    fe.IsDeleted == true),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Should retire the file version
        _manifestManagerMock.Verify(
            x => x.SaveFileVersionAsync(
                It.Is<FileVersion>(fv =>
                    fv.UniqueFileId == fileId &&
                    fv.State == FileVersionState.Retired),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_WithAlreadyDeletedFile_SkipsProcessing()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var deletedPath = "documents/deleted.txt";

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry>(), new List<string> { deletedPath });
        SetupManifestDownload(manifest);

        // File already deleted
        var existingFile = new FileEntry
        {
            DeviceId = deviceId,
            RelativePath = deletedPath,
            CurrentVersionId = "abc123",
            Size = 1024,
            LastWriteUtc = DateTimeOffset.UtcNow.AddDays(-1),
            LastBackupRunId = Guid.NewGuid().ToString("N"),
            IsDeleted = true // Already deleted
        };

        _manifestManagerMock
            .Setup(x => x.GetFileEntryAsync(deviceId, deletedPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingFile);

        // Act
        await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert - should not update file version
        _manifestManagerMock.Verify(
            x => x.GetFileVersionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region CommitJob Status Updates

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_UpdatesCommitJobToProcessing_BeforeWork()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, new List<ManifestFileEntry>(), new List<string>());
        SetupManifestDownload(manifest);

        var updateSequence = new List<CommitJobStatus>();

        _manifestManagerMock
            .Setup(x => x.UpdateCommitJobAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()))
            .Callback<CommitJob, CancellationToken>((job, ct) => updateSequence.Add(job.Status))
            .ReturnsAsync((CommitJob job, CancellationToken ct) => job);

        // Act
        await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert
        _manifestManagerMock.Verify(
            x => x.TryClaimCommitJobAsync(commitId, It.IsAny<CancellationToken>()),
            Times.Once);

        updateSequence.Should().HaveCountGreaterOrEqualTo(1);
        updateSequence.Last().Should().Be(CommitJobStatus.Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_OnSuccess_UpdatesCommitJobWithFileCount()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var files = new List<ManifestFileEntry>
        {
            CreateFileEntry("file1.txt", "id1"),
            CreateFileEntry("file2.txt", "id2"),
            CreateFileEntry("file3.txt", "id3")
        };

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        var manifest = CreateManifest(deviceId, runId, files, new List<string>());
        SetupManifestDownload(manifest);

        foreach (var file in files)
        {
            _manifestManagerMock
                .Setup(x => x.GetFileEntryAsync(deviceId, file.RelativePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync((FileEntry?)null);
        }

        // Act
        await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert
        _manifestManagerMock.Verify(
            x => x.UpdateCommitJobAsync(
                It.Is<CommitJob>(job =>
                    job.Status == CommitJobStatus.Succeeded &&
                    job.FilesProcessed == 3 &&
                    job.CompletedAt != null),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessBackupRunAsync_OnError_UpdatesCommitJobToFailed()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var backupEvent = CreateBackupEvent(deviceId, runId, commitId);
        SetupQueuedCommitJob(commitId, deviceId, runId);
        SetupBackupRun(deviceId, runId);

        // Setup manifest blob to throw exception during processing
        var manifestPath = $"runs/{deviceId:N}/{runId:N}/run-manifest.json";
        var manifestBlobClient = new Mock<BlobClient>();
        _blobClients[manifestPath] = manifestBlobClient;

        manifestBlobClient
            .Setup(x => x.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Blob service error"));

        // Act
        var act = async () => await _sut.ProcessBackupRunAsync(backupEvent);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();

        // The commit job is updated twice: once to Processing, once to Failed
        // We verify that it was updated to Failed at least once
        _manifestManagerMock.Verify(
            x => x.UpdateCommitJobAsync(
                It.Is<CommitJob>(job =>
                    job.Status == CommitJobStatus.Failed &&
                    job.Error != null &&
                    job.CompletedAt != null),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    #endregion

    #region Helper Methods

    private BackupRunCommittedEvent CreateBackupEvent(Guid deviceId, Guid runId, Guid commitId)
    {
        return new BackupRunCommittedEvent
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CommittedAt = DateTimeOffset.UtcNow,
            StagingPath = $"staging/{deviceId:N}/{runId:N}/",
            ManifestPath = $"runs/{deviceId:N}/{runId:N}/run-manifest.json"
        };
    }

    private void SetupQueuedCommitJob(Guid commitId, Guid deviceId, Guid runId)
    {
        var commitJob = new CommitJob
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            Status = CommitJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _manifestManagerMock
            .Setup(x => x.GetCommitJobAsync(commitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(commitJob);

        _manifestManagerMock
            .Setup(x => x.TryClaimCommitJobAsync(commitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new CommitJob
            {
                CommitId = commitId,
                DeviceId = deviceId,
                RunId = runId,
                Status = CommitJobStatus.Processing,
                CreatedAt = commitJob.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow
            }));

        _manifestManagerMock
            .Setup(x => x.UpdateCommitJobAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommitJob job, CancellationToken ct) => job);

        _manifestManagerMock
            .Setup(x => x.GetCommitFileProgressAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommitFileProgress?)null);

        _manifestManagerMock
            .Setup(x => x.SaveCommitFileProgressAsync(It.IsAny<CommitFileProgress>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommitFileProgress progress, CancellationToken ct) => progress);
    }

    private void SetupBackupRun(Guid deviceId, Guid runId)
    {
        var backupRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Status = BackupRunStatus.Queued
        };

        _manifestManagerMock
            .Setup(x => x.GetBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRun);

        _manifestManagerMock
            .Setup(x => x.UpdateBackupRunAsync(deviceId, runId, It.IsAny<BackupRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid d, Guid r, BackupRun run, CancellationToken ct) => run);
    }

    private RunManifest CreateManifest(Guid deviceId, Guid runId, List<ManifestFileEntry> files, List<string> deleted)
    {
        return new RunManifest
        {
            DeviceId = deviceId.ToString("N"),
            RunId = runId.ToString("N"),
            Files = files,
            Deleted = deleted
        };
    }

    private void SetupManifestDownload(RunManifest manifest, Action<string>? pathCallback = null)
    {
        var manifestJson = JsonSerializer.Serialize(manifest);

        var downloadResult = BlobsModelFactory.BlobDownloadResult(
            content: BinaryData.FromString(manifestJson));

        var response = Response.FromValue(downloadResult, Mock.Of<Response>());

        // Find or create the manifest blob client in the dictionary
        var manifestPath = $"runs/{manifest.DeviceId}/{manifest.RunId}/run-manifest.json";
        if (!_blobClients.ContainsKey(manifestPath))
        {
            _blobClients[manifestPath] = new Mock<BlobClient>();
        }

        var manifestBlobClient = _blobClients[manifestPath];
        manifestBlobClient
            .Setup(x => x.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(ct => pathCallback?.Invoke(manifestPath))
            .ReturnsAsync(response);

        foreach (var fileEntry in manifest.Files)
        {
            SetupStagedBlobProperties(manifest.DeviceId, manifest.RunId, fileEntry);
        }
    }

    private void SetupStagedBlobProperties(
        string deviceId,
        string runId,
        ManifestFileEntry fileEntry,
        long? contentLength = null,
        string? sha256 = null,
        bool includeSha256Metadata = true)
    {
        var sourcePath = $"staging/{deviceId}/{runId}/{fileEntry.UniqueFileId}";
        if (!_blobClients.ContainsKey(sourcePath))
        {
            _blobClients[sourcePath] = new Mock<BlobClient>();
        }

        var metadata = new Dictionary<string, string>();
        if (includeSha256Metadata)
        {
            metadata[BackupBlobMetadata.Sha256] = sha256 ?? fileEntry.Sha256;
        }

        var properties = BlobsModelFactory.BlobProperties(
            contentLength: contentLength ?? fileEntry.Size,
            metadata: metadata);
        var response = Response.FromValue(properties, Mock.Of<Response>());

        _blobClients[sourcePath]
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
    }

    private ManifestFileEntry CreateFileEntry(string relativePath, string uniqueFileId)
    {
        return new ManifestFileEntry
        {
            RelativePath = relativePath,
            UniqueFileId = uniqueFileId,
            Sha256 = $"sha256_{uniqueFileId}",
            Size = 1024,
            Mtime = DateTimeOffset.UtcNow
        };
    }

    #endregion
}
