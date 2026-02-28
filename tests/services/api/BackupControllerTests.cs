using FluentAssertions;
using FlorisDeV.BackupApi.Controllers;
using FlorisDeV.BackupApi.Models.Api.Requests;
using FlorisDeV.BackupApi.Models.Api.Responses;
using FlorisDeV.BackupApi.Models.Application;
using FlorisDeV.BackupApi.Models.Infrastructure;
using FlorisDeV.BackupApi.Models.State;
using FlorisDeV.BackupApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Tests for BackupController to verify API endpoint behavior, validation, and response mapping.
/// </summary>
public class BackupControllerTests
{
    private readonly Mock<IBackupRunService> _backupRunServiceMock;
    private readonly Mock<ILogger<BackupController>> _loggerMock;
    private readonly BackupController _sut;

    public BackupControllerTests()
    {
        _backupRunServiceMock = new Mock<IBackupRunService>();
        _loggerMock = new Mock<ILogger<BackupController>>();

        _sut = new BackupController(
            _backupRunServiceMock.Object,
            _loggerMock.Object);
    }

    #region StartBackupRun Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRun_WithValidRequest_ReturnsOkWithStartResponse()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;

        var request = new StartBackupRunRequest
        {
            DeviceId = deviceId
        };

        var backupRunResult = new BackupRunStartResult
        {
            Run = new BackupRun
            {
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = startedAt,
                Status = BackupRunStatus.Queued
            },
            SasUrl = new SasUrlInfo
            {
                Url = new Uri("https://storage.blob.core.windows.net/backups/staging/device/run?sig=token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                TtlMinutes = 60
            }
        };

        _backupRunServiceMock
            .Setup(x => x.StartBackupRunAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRunResult);

        // Act
        var result = await _sut.StartBackupRun(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        okResult!.Value.Should().BeOfType<StartBackupRunResponse>();

        var response = okResult.Value as StartBackupRunResponse;
        response!.DeviceId.Should().Be(deviceId);
        response.RunId.Should().Be(runId);
        response.StartedAt.Should().Be(startedAt);
        response.Status.Should().Be(BackupRunStatus.Queued);
        response.SasUrlInfo.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRun_MapsAllFieldsCorrectly()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var sasUrl = new Uri("https://storage.blob.core.windows.net/backups/staging/device/run?sig=token");
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var request = new StartBackupRunRequest { DeviceId = deviceId };

        var backupRunResult = new BackupRunStartResult
        {
            Run = new BackupRun
            {
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = startedAt,
                Status = BackupRunStatus.Queued
            },
            SasUrl = new SasUrlInfo
            {
                Url = sasUrl,
                ExpiresAt = expiresAt,
                TtlMinutes = 60
            }
        };

        _backupRunServiceMock
            .Setup(x => x.StartBackupRunAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRunResult);

        // Act
        var result = await _sut.StartBackupRun(request, CancellationToken.None);
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as StartBackupRunResponse;

        // Assert
        response!.SasUrlInfo.Url.Should().Be(sasUrl);
        response.SasUrlInfo.ExpiresAt.Should().Be(expiresAt);
        response.SasUrlInfo.TtlMinutes.Should().Be(60);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRun_CallsServiceWithCorrectDeviceId()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var request = new StartBackupRunRequest { DeviceId = deviceId };

        _backupRunServiceMock
            .Setup(x => x.StartBackupRunAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupRunStartResult
            {
                Run = new BackupRun
                {
                    DeviceId = deviceId,
                    RunId = Guid.NewGuid(),
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = BackupRunStatus.Queued
                },
                SasUrl = new SasUrlInfo
                {
                    Url = new Uri("https://storage.blob.core.windows.net/backups/staging/device/run?sig=token"),
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    TtlMinutes = 60
                }
            });

        // Act
        await _sut.StartBackupRun(request, CancellationToken.None);

        // Assert
        _backupRunServiceMock.Verify(
            x => x.StartBackupRunAsync(deviceId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRun_PassesCancellationTokenToService()
    {
        // Arrange
        var request = new StartBackupRunRequest { DeviceId = Guid.NewGuid() };
        var cts = new CancellationTokenSource();

        _backupRunServiceMock
            .Setup(x => x.StartBackupRunAsync(It.IsAny<Guid>(), cts.Token))
            .ReturnsAsync(new BackupRunStartResult
            {
                Run = new BackupRun
                {
                    DeviceId = request.DeviceId,
                    RunId = Guid.NewGuid(),
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = BackupRunStatus.Queued
                },
                SasUrl = new SasUrlInfo
                {
                    Url = new Uri("https://storage.blob.core.windows.net/backups/staging/device/run?sig=token"),
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    TtlMinutes = 60
                }
            });

        // Act
        await _sut.StartBackupRun(request, cts.Token);

        // Assert
        _backupRunServiceMock.Verify(
            x => x.StartBackupRunAsync(It.IsAny<Guid>(), cts.Token),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRun_WhenServiceThrows_PropagatesException()
    {
        // Arrange
        var request = new StartBackupRunRequest { DeviceId = Guid.NewGuid() };
        var expectedException = new InvalidOperationException("Service error");

        _backupRunServiceMock
            .Setup(x => x.StartBackupRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act
        var act = async () => await _sut.StartBackupRun(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Service error");
    }

    #endregion

    #region CommitBackupRun Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRun_WithValidRequest_ReturnsOk()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var request = new CommitBackupRunRequest
        {
            DeviceId = deviceId,
            RunId = runId
        };

        _backupRunServiceMock
            .Setup(x => x.CommitBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupRun
            {
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Succeeded
            });

        // Act
        var result = await _sut.CommitBackupRun(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRun_CallsServiceWithCorrectParameters()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var request = new CommitBackupRunRequest
        {
            DeviceId = deviceId,
            RunId = runId
        };

        _backupRunServiceMock
            .Setup(x => x.CommitBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupRun
            {
                DeviceId = deviceId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Succeeded
            });

        // Act
        await _sut.CommitBackupRun(request, CancellationToken.None);

        // Assert
        _backupRunServiceMock.Verify(
            x => x.CommitBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRun_PassesCancellationTokenToService()
    {
        // Arrange
        var request = new CommitBackupRunRequest
        {
            DeviceId = Guid.NewGuid(),
            RunId = Guid.NewGuid()
        };
        var cts = new CancellationTokenSource();

        _backupRunServiceMock
            .Setup(x => x.CommitBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), cts.Token))
            .ReturnsAsync(new BackupRun
            {
                DeviceId = request.DeviceId,
                RunId = request.RunId,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow,
                Status = BackupRunStatus.Succeeded
            });

        // Act
        await _sut.CommitBackupRun(request, cts.Token);

        // Assert
        _backupRunServiceMock.Verify(
            x => x.CommitBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), cts.Token),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRun_WhenServiceThrows_PropagatesException()
    {
        // Arrange
        var request = new CommitBackupRunRequest
        {
            DeviceId = Guid.NewGuid(),
            RunId = Guid.NewGuid()
        };
        var expectedException = new InvalidOperationException("Commit failed");

        _backupRunServiceMock
            .Setup(x => x.CommitBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act
        var act = async () => await _sut.CommitBackupRun(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Commit failed");
    }

    #endregion
}
