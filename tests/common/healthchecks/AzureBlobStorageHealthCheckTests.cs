using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using FlorisDeV.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;

namespace FlorisDeV.HealthChecks.Tests;

/// <summary>
/// Tests for AzureBlobStorageHealthCheck to verify connectivity checks and error handling.
/// </summary>
public class AzureBlobStorageHealthCheckTests
{
    private readonly Mock<BlobServiceClient> _mockBlobServiceClient;
    private readonly AzureBlobStorageHealthCheck _sut;

    public AzureBlobStorageHealthCheckTests()
    {
        _mockBlobServiceClient = new Mock<BlobServiceClient>();
        _sut = new AzureBlobStorageHealthCheck(_mockBlobServiceClient.Object);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WhenStorageAccessible_ReturnsHealthy()
    {
        // Arrange
        var accountInfo = BlobsModelFactory.AccountInfo(
            SkuName.StandardLrs,
            AccountKind.StorageV2);

        var response = Response.FromValue(accountInfo, Mock.Of<Response>());

        _mockBlobServiceClient
            .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("Azure Blob Storage is accessible.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WhenStorageAccessible_IncludesAccountInfoInData()
    {
        // Arrange
        var accountInfo = BlobsModelFactory.AccountInfo(
            SkuName.PremiumLrs,
            AccountKind.BlobStorage);

        var response = Response.FromValue(accountInfo, Mock.Of<Response>());

        _mockBlobServiceClient
            .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Data.Should().ContainKey("AccountKind");
        result.Data["AccountKind"].Should().Be("BlobStorage");
        result.Data.Should().ContainKey("SkuName");
        result.Data["SkuName"].Should().Be("PremiumLrs");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WhenStorageThrowsException_ReturnsUnhealthy()
    {
        // Arrange
        var exception = new RequestFailedException("Connection failed");

        _mockBlobServiceClient
            .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Azure Blob Storage is not accessible.");
        result.Exception.Should().Be(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WhenStorageThrowsException_UsesRegistrationFailureStatus()
    {
        // Arrange
        var exception = new InvalidOperationException("Service error");

        _mockBlobServiceClient
            .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                _sut,
                HealthStatus.Degraded, // Custom failure status
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_RespectsCancellationToken()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockBlobServiceClient
            .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context, cts.Token);

        // Assert
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<TaskCanceledException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WithDifferentAccountTypes_ReportsCorrectly()
    {
        // Arrange
        var testCases = new[]
        {
            (AccountKind.Storage, SkuName.StandardGrs),
            (AccountKind.StorageV2, SkuName.StandardLrs),
            (AccountKind.BlobStorage, SkuName.PremiumLrs),
            (AccountKind.BlockBlobStorage, SkuName.StandardZrs)
        };

        foreach (var (accountKind, skuName) in testCases)
        {
            var accountInfo = BlobsModelFactory.AccountInfo(skuName, accountKind);
            var response = Response.FromValue(accountInfo, Mock.Of<Response>());

            _mockBlobServiceClient
                .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(response);

            var context = new HealthCheckContext
            {
                Registration = new HealthCheckRegistration(
                    "test",
                    _sut,
                    HealthStatus.Unhealthy,
                    null)
            };

            // Act
            var result = await _sut.CheckHealthAsync(context);

            // Assert
            result.Status.Should().Be(HealthStatus.Healthy, 
                $"check should be healthy for {accountKind}");
            result.Data["AccountKind"].Should().Be(accountKind.ToString());
            result.Data["SkuName"].Should().Be(skuName.ToString());
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WhenNetworkError_IncludesExceptionDetails()
    {
        // Arrange
        var exception = new RequestFailedException(
            503,
            "Service Unavailable",
            "ServiceUnavailable",
            null);

        _mockBlobServiceClient
            .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Exception.Should().NotBeNull();
        result.Exception.Should().BeOfType<RequestFailedException>();
        result.Exception!.Message.Should().Contain("Service Unavailable");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_PassesCancellationTokenToService()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        CancellationToken? capturedToken = null;

        var accountInfo = BlobsModelFactory.AccountInfo(
            SkuName.StandardLrs,
            AccountKind.StorageV2);
        var response = Response.FromValue(accountInfo, Mock.Of<Response>());

        _mockBlobServiceClient
            .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(ct => capturedToken = ct)
            .ReturnsAsync(response);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        await _sut.CheckHealthAsync(context, cts.Token);

        // Assert
        capturedToken.Should().NotBeNull();
        capturedToken!.Value.CanBeCanceled.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WithTimeoutException_ReturnsUnhealthy()
    {
        // Arrange
        var exception = new TimeoutException("Storage request timed out");

        _mockBlobServiceClient
            .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<TimeoutException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_CallsGetAccountInfoOnce()
    {
        // Arrange
        var accountInfo = BlobsModelFactory.AccountInfo(
            SkuName.StandardLrs,
            AccountKind.StorageV2);
        var response = Response.FromValue(accountInfo, Mock.Of<Response>());

        _mockBlobServiceClient
            .Setup(x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        await _sut.CheckHealthAsync(context);

        // Assert
        _mockBlobServiceClient.Verify(
            x => x.GetAccountInfoAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
