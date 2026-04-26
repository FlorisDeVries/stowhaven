using FluentAssertions;
using FlorisDeV.BackupApi.Controllers;
using FlorisDeV.BackupApi.Options;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.Application;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Security.Claims;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Tests for BackupController to verify API endpoint behavior, validation, and response mapping.
/// </summary>
public class BackupControllerTests
{
    private readonly Mock<IBackupRunService> _backupRunServiceMock;
    private readonly Mock<IDeviceAuthorizationService> _deviceAuthorizationServiceMock;
    private readonly SasSecurityOptions _sasSecurityOptions;
    private readonly Mock<ILogger<BackupController>> _loggerMock;
    private readonly BackupController _sut;

    public BackupControllerTests()
    {
        _backupRunServiceMock = new Mock<IBackupRunService>();
        _deviceAuthorizationServiceMock = new Mock<IDeviceAuthorizationService>();
        _sasSecurityOptions = new SasSecurityOptions();
        _loggerMock = new Mock<ILogger<BackupController>>();

        _deviceAuthorizationServiceMock
            .Setup(x => x.AuthorizeDeviceAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new BackupController(
            _backupRunServiceMock.Object,
            _deviceAuthorizationServiceMock.Object,
            Microsoft.Extensions.Options.Options.Create(_sasSecurityOptions),
            _loggerMock.Object);

        // Setup HttpContext with mock connection for RemoteIpAddress
        var mockHttpContext = new DefaultHttpContext();
        mockHttpContext.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        mockHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", "test-tenant"),
            new Claim("oid", "test-user")
        }, "Test"));
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = mockHttpContext
        };
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
            .Setup(x => x.StartBackupRunAsync(deviceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRunResult);

        // Act
        var result = await _sut.StartBackupRun(deviceId, CancellationToken.None);

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
            .Setup(x => x.StartBackupRunAsync(deviceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(backupRunResult);

        // Act
        var result = await _sut.StartBackupRun(deviceId, CancellationToken.None);
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

        _backupRunServiceMock
            .Setup(x => x.StartBackupRunAsync(deviceId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        await _sut.StartBackupRun(deviceId, CancellationToken.None);

        // Assert
        _backupRunServiceMock.Verify(
            x => x.StartBackupRunAsync(deviceId, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRun_PassesCancellationTokenToService()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var cts = new CancellationTokenSource();

        _backupRunServiceMock
            .Setup(x => x.StartBackupRunAsync(deviceId, It.IsAny<string>(), cts.Token))
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
        await _sut.StartBackupRun(deviceId, cts.Token);

        // Assert
        _backupRunServiceMock.Verify(
            x => x.StartBackupRunAsync(deviceId, null, cts.Token),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRun_WhenSasIpRestrictionEnabled_PassesRemoteIpToService()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        _sasSecurityOptions.EnableIpRestriction = true;

        _backupRunServiceMock
            .Setup(x => x.StartBackupRunAsync(deviceId, "127.0.0.1", It.IsAny<CancellationToken>()))
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
        await _sut.StartBackupRun(deviceId, CancellationToken.None);

        // Assert
        _backupRunServiceMock.Verify(
            x => x.StartBackupRunAsync(deviceId, "127.0.0.1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartBackupRun_WhenServiceThrows_PropagatesException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var expectedException = new InvalidOperationException("Service error");

        _backupRunServiceMock
            .Setup(x => x.StartBackupRunAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act
        var act = async () => await _sut.StartBackupRun(deviceId, CancellationToken.None);

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
            RunId = runId
        };

        _backupRunServiceMock
            .Setup(x => x.CommitBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitJob
            {
                CommitId = Guid.NewGuid(),
                DeviceId = deviceId,
                RunId = request.RunId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow
            });

        // Act
        var result = await _sut.CommitBackupRun(deviceId, request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<AcceptedAtActionResult>();

        var acceptedResult = result.Result as AcceptedAtActionResult;
        acceptedResult!.Value.Should().BeOfType<CommitBackupRunResponse>();
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
            RunId = runId
        };

        _backupRunServiceMock
            .Setup(x => x.CommitBackupRunAsync(deviceId, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitJob
            {
                CommitId = Guid.NewGuid(),
                DeviceId = deviceId,
                RunId = request.RunId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow
            });

        // Act
        await _sut.CommitBackupRun(deviceId, request, CancellationToken.None);

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
        var deviceId = Guid.NewGuid();
        var request = new CommitBackupRunRequest
        {
            RunId = Guid.NewGuid()
        };
        var cts = new CancellationTokenSource();

        _backupRunServiceMock
            .Setup(x => x.CommitBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), cts.Token))
            .ReturnsAsync(new CommitJob
            {
                CommitId = Guid.NewGuid(),
                DeviceId = deviceId,
                RunId = request.RunId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow
            });

        // Act
        await _sut.CommitBackupRun(deviceId, request, cts.Token);

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
        var deviceId = Guid.NewGuid();
        var request = new CommitBackupRunRequest
        {
            RunId = Guid.NewGuid()
        };
        var expectedException = new InvalidOperationException("Commit failed");

        _backupRunServiceMock
            .Setup(x => x.CommitBackupRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act
        var act = async () => await _sut.CommitBackupRun(deviceId, request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Commit failed");
    }

    #endregion
}
