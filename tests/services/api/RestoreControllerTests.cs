using System.Net;
using System.Security.Claims;
using FluentAssertions;
using FlorisDeV.BackupApi.Controllers;
using FlorisDeV.BackupApi.Options;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;

namespace FlorisDeV.BackupApi.Tests;

public class RestoreControllerTests
{
    private readonly Mock<IRestoreService> _restoreService = new();
    private readonly Mock<IDeviceAuthorizationService> _authorizationService = new();
    private readonly SasSecurityOptions _sasSecurityOptions = new();
    private readonly RestoreController _sut;

    public RestoreControllerTests()
    {
        _authorizationService
            .Setup(x => x.AuthorizeDeviceAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new RestoreController(
            _restoreService.Object,
            _authorizationService.Object,
            Microsoft.Extensions.Options.Options.Create(_sasSecurityOptions));

        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "test-user")], "Test"))
            }
        };
        _sut.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListRestoreFiles_AuthorizesDeviceAndReturnsFiles()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var response = new ListRestoreFilesResponse
        {
            DeviceId = deviceId,
            Files = [],
            PageSize = 50,
            ContinuationToken = "cursor",
            NextContinuationToken = "next"
        };
        _restoreService.Setup(x => x.ListRestoreFilesAsync(deviceId, 50, "cursor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _sut.ListRestoreFiles(deviceId, pageSize: 50, continuationToken: "cursor", cancellationToken: CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(response);
        _authorizationService.Verify(x => x.AuthorizeDeviceAsync(It.IsAny<ClaimsPrincipal>(), deviceId, It.IsAny<CancellationToken>()), Times.Once);
        _restoreService.Verify(x => x.ListRestoreFilesAsync(deviceId, 50, "cursor", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartRestore_WhenSasIpRestrictionEnabled_PassesClientIpToService()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var request = new StartRestoreRequest { LogicalPaths = ["documents/a.txt"] };
        var response = new StartRestoreResponse
        {
            RestoreId = Guid.NewGuid(),
            DeviceId = deviceId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            SasUrlInfo = new SasUrlInfo
            {
                Url = new Uri("https://storage.example/backups?sas=1"),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                TtlMinutes = 60
            },
            Files = []
        };
        _sasSecurityOptions.EnableIpRestriction = true;
        _restoreService.Setup(x => x.StartRestoreAsync(deviceId, request, "127.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _sut.StartRestore(deviceId, request, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(response);
        _restoreService.Verify(x => x.StartRestoreAsync(deviceId, request, "127.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
