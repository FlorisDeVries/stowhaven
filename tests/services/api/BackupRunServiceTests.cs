using System.Diagnostics;
using FluentAssertions;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.State;
using Moq;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Tests for BackupRunService to verify workflow orchestration between ManifestManager and SasUrlService.
/// </summary>
public class BackupRunServiceTests
{
    private readonly Mock<IManifestManager> _manifestManagerMock;
    private readonly Mock<ISasUrlService> _sasUrlServiceMock;
    private readonly Mock<TelemetryProvider> _telemetryMock;
    private readonly BackupRunService _sut;

    private readonly Mock<IBackupEventPublisher> _eventPublisherMock;

    public BackupRunServiceTests()
    {
        _manifestManagerMock = new Mock<IManifestManager>();
        _sasUrlServiceMock = new Mock<ISasUrlService>();
        _eventPublisherMock = new Mock<IBackupEventPublisher>();
        _telemetryMock = new Mock<TelemetryProvider>();

        _sut = new BackupRunService(
            _manifestManagerMock.Object,
            _sasUrlServiceMock.Object,
            _eventPublisherMock.Object,
            _telemetryMock.Object);
    }

    #region StartBackupRunAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRunAsync_WithValidDeviceId_CreatesBackupRunAndGeneratesSasUrl()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;

        var backupRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = startedAt,
            Status = BackupRunStatus.Queued
        };

        _manifestManagerMock
            .Setup(x => x.CreateBackupRunAsync(
                deviceId,
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRun);

        _sasUrlServiceMock
            .Setup(x => x.GenerateUploadSasUrlAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                60,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SasUrlInfo
            {
                Url = new Uri("https://storage.blob.core.windows.net/backups/staging/device/run?sig=token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                TtlMinutes = 60
            });

        // Act
        var result = await _sut.StartBackupRunAsync(deviceId);

        // Assert
        result.Should().NotBeNull();
        result.Run.Should().Be(backupRun);
        result.SasUrl.Should().NotBeNull();
        result.Run.DeviceId.Should().Be(deviceId);
        result.Run.Status.Should().Be(BackupRunStatus.Queued);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRunAsync_GeneratesSasUrlWithCorrectPath()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var backupRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = BackupRunStatus.Queued
        };

        _manifestManagerMock
            .Setup(x => x.CreateBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRun);

        var capturedPaths = new List<string>();
        _sasUrlServiceMock
            .Setup(x => x.GenerateUploadSasUrlAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, CancellationToken>((path, ip, ttl, ct) => capturedPaths.Add(path))
            .ReturnsAsync(new SasUrlInfo
            {
                Url = new Uri("https://storage.blob.core.windows.net/backups/staging/device/run?sig=token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                TtlMinutes = 60
            });

        // Act
        await _sut.StartBackupRunAsync(deviceId);

        // Assert
        capturedPaths.Should().Contain(path => path.StartsWith("staging/", StringComparison.Ordinal));
        capturedPaths.Should().Contain(path => path.StartsWith("runs/", StringComparison.Ordinal));
        capturedPaths.Should().OnlyContain(path => path.Contains(deviceId.ToString("N")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRunAsync_GeneratesSasUrlWith60MinuteTtl()
    {
        // Arrange
        var deviceId = Guid.NewGuid();

        _manifestManagerMock
            .Setup(x => x.CreateBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupRun
            {
                DeviceId = deviceId,
                RunId = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Queued
            });

        _sasUrlServiceMock
            .Setup(x => x.GenerateUploadSasUrlAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                60,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SasUrlInfo
            {
                Url = new Uri("https://storage.blob.core.windows.net/backups/staging/device/run?sig=token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                TtlMinutes = 60
            });

        // Act
        await _sut.StartBackupRunAsync(deviceId);

        // Assert
        _sasUrlServiceMock.Verify(
            x => x.GenerateUploadSasUrlAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                60,
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRunAsync_CallsManifestManagerWithCorrectParameters()
    {
        // Arrange
        var deviceId = Guid.NewGuid();

        _manifestManagerMock
            .Setup(x => x.CreateBackupRunAsync(
                deviceId,
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupRun
            {
                DeviceId = deviceId,
                RunId = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Queued
            });

        _sasUrlServiceMock
            .Setup(x => x.GenerateUploadSasUrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SasUrlInfo
            {
                Url = new Uri("https://storage.blob.core.windows.net/backups/staging/device/run?sig=token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                TtlMinutes = 60
            });

        // Act
        await _sut.StartBackupRunAsync(deviceId);

        // Assert
        _manifestManagerMock.Verify(
            x => x.CreateBackupRunAsync(
                deviceId,
                It.Is<Guid>(g => g != Guid.Empty),
                It.Is<DateTimeOffset>(dt => dt <= DateTimeOffset.UtcNow),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRunAsync_WhenManifestManagerFails_PropagatesException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var expectedException = new InvalidOperationException("State store unavailable");

        _manifestManagerMock
            .Setup(x => x.CreateBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act
        var act = async () => await _sut.StartBackupRunAsync(deviceId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("State store unavailable");

        // Should not call SAS service if manifest creation fails
        _sasUrlServiceMock.Verify(
            x => x.GenerateUploadSasUrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRunAsync_WhenSasGenerationFails_PropagatesException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();

        _manifestManagerMock
            .Setup(x => x.CreateBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupRun
            {
                DeviceId = deviceId,
                RunId = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Queued
            });

        var expectedException = new InvalidOperationException("Storage account unavailable");
        _sasUrlServiceMock
            .Setup(x => x.GenerateUploadSasUrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act
        var act = async () => await _sut.StartBackupRunAsync(deviceId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Storage account unavailable");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRunAsync_WithCancellationToken_PassesToDependencies()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var cts = new CancellationTokenSource();

        _manifestManagerMock
            .Setup(x => x.CreateBackupRunAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                cts.Token))
            .ReturnsAsync(new BackupRun
            {
                DeviceId = deviceId,
                RunId = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Queued
            });

        _sasUrlServiceMock
            .Setup(x => x.GenerateUploadSasUrlAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                cts.Token))
            .ReturnsAsync(new SasUrlInfo
            {
                Url = new Uri("https://storage.blob.core.windows.net/backups/staging/device/run?sig=token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                TtlMinutes = 60
            });

        // Act
        await _sut.StartBackupRunAsync(deviceId, null, cts.Token);

        // Assert
        _manifestManagerMock.Verify(
            x => x.CreateBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), cts.Token),
            Times.Once);
        _sasUrlServiceMock.Verify(
            x => x.GenerateUploadSasUrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), cts.Token),
            Times.Exactly(2));
    }

    #endregion

    #region CommitBackupRunAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_WithValidIds_CommitsRun()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitId = Guid.NewGuid();

        var commitJob = new CommitJob
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = runId,
            Status = CommitJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var backupRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Status = BackupRunStatus.Queued
        };

        _manifestManagerMock
            .Setup(x => x.GetBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRun);

        _manifestManagerMock
            .Setup(x => x.CreateCommitJobAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(commitJob);

        _eventPublisherMock
            .Setup(x => x.PublishBackupRunCommittedAsync(It.IsAny<CommitJob>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.CommitBackupRunAsync(deviceId, runId);

        // Assert
        result.Should().NotBeNull();
        result.DeviceId.Should().Be(deviceId);
        result.RunId.Should().Be(runId);
        result.Status.Should().Be(CommitJobStatus.Queued);
        result.CommitId.Should().Be(commitId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_CallsManifestManagerOnce()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var backupRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Status = BackupRunStatus.Queued
        };

        _manifestManagerMock
            .Setup(x => x.GetBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRun);

        _manifestManagerMock
            .Setup(x => x.CreateCommitJobAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitJob
            {
                CommitId = Guid.NewGuid(),
                DeviceId = deviceId,
                RunId = runId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            });

        _eventPublisherMock
            .Setup(x => x.PublishBackupRunCommittedAsync(It.IsAny<CommitJob>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.CommitBackupRunAsync(deviceId, runId);

        // Assert
        _manifestManagerMock.Verify(
            x => x.CreateCommitJobAsync(deviceId, runId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_WhenManifestManagerFails_PropagatesException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var expectedException = new InvalidOperationException("Commit failed");

        _manifestManagerMock
            .Setup(x => x.GetBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act
        var act = async () => await _sut.CommitBackupRunAsync(deviceId, runId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Commit failed");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_WithCancellationToken_PassesToManifestManager()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var cts = new CancellationTokenSource();

        var backupRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Status = BackupRunStatus.Queued
        };

        _manifestManagerMock
            .Setup(x => x.GetBackupRunAsync(deviceId, runId, cts.Token))
            .ReturnsAsync(backupRun);

        _manifestManagerMock
            .Setup(x => x.CreateCommitJobAsync(deviceId, runId, cts.Token))
            .ReturnsAsync(new CommitJob
            {
                CommitId = Guid.NewGuid(),
                DeviceId = deviceId,
                RunId = runId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            });

        _eventPublisherMock
            .Setup(x => x.PublishBackupRunCommittedAsync(It.IsAny<CommitJob>(), It.IsAny<string>(), cts.Token))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.CommitBackupRunAsync(deviceId, runId, null, cts.Token);

        // Assert
        _manifestManagerMock.Verify(
            x => x.CreateCommitJobAsync(deviceId, runId, cts.Token),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_CreatesCommitJobWithTimestamp()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var backupRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Status = BackupRunStatus.Queued
        };

        var commitJob = new CommitJob
        {
            CommitId = Guid.NewGuid(),
            DeviceId = deviceId,
            RunId = runId,
            Status = CommitJobStatus.Queued,
            CreatedAt = createdAt
        };

        _manifestManagerMock
            .Setup(x => x.GetBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRun);

        _manifestManagerMock
            .Setup(x => x.CreateCommitJobAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(commitJob);

        _eventPublisherMock
            .Setup(x => x.PublishBackupRunCommittedAsync(It.IsAny<CommitJob>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.CommitBackupRunAsync(deviceId, runId);

        // Assert
        result.CreatedAt.Should().BeCloseTo(createdAt, TimeSpan.FromSeconds(1));
        result.Status.Should().Be(CommitJobStatus.Queued);
    }

    #endregion
}
