using Dapr.Client;
using FluentAssertions;
using FlorisDeV.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;

namespace FlorisDeV.HealthChecks.Tests;

/// <summary>
/// Tests for DaprHealthCheck to verify Dapr sidecar health monitoring.
/// </summary>
public class DaprHealthCheckTests
{
    private readonly Mock<DaprClient> _mockDaprClient;
    private readonly DaprHealthCheck _sut;

    public DaprHealthCheckTests()
    {
        _mockDaprClient = new Mock<DaprClient>();
        _sut = new DaprHealthCheck(_mockDaprClient.Object, Options.Create(new DaprHealthCheckOptions
        {
            EnableStateStoreProbes = false,
            EnablePubSubProbe = false
        }));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WhenDaprIsHealthy_ReturnsHealthyStatus()
    {
        // Arrange
        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("Dapr sidecar and configured components are healthy.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WhenDaprIsUnhealthy_ReturnsUnhealthyStatus()
    {
        // Arrange
        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Dapr sidecar is unhealthy.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WhenDaprIsUnhealthy_UsesRegistrationFailureStatus()
    {
        // Arrange
        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
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
    public async Task CheckHealthAsync_PassesCancellationToken()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        CancellationToken? capturedToken = null;

        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(ct => capturedToken = ct)
            .ReturnsAsync(true);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
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
    public async Task CheckHealthAsync_WhenDaprThrowsException_PropagatesException()
    {
        // Arrange
        var exception = new InvalidOperationException("Dapr not available");

        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var act = async () => await _sut.CheckHealthAsync(context);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Dapr not available");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var act = async () => await _sut.CheckHealthAsync(context, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_CallsCheckHealthOnce()
    {
        // Arrange
        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        await _sut.CheckHealthAsync(context);

        // Assert
        _mockDaprClient.Verify(
            x => x.CheckHealthAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_MultipleSequentialCalls_IndependentResults()
    {
        // Arrange
        _mockDaprClient
            .SetupSequence(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result1 = await _sut.CheckHealthAsync(context);
        var result2 = await _sut.CheckHealthAsync(context);
        var result3 = await _sut.CheckHealthAsync(context);

        // Assert
        result1.Status.Should().Be(HealthStatus.Healthy);
        result2.Status.Should().Be(HealthStatus.Unhealthy);
        result3.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_IncludesSidecarDataInResult()
    {
        // Arrange
        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Data.Should().ContainKey("sidecar");
        result.Data["sidecar"].Should().Be("healthy");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_DoesNotIncludeExceptionWhenHealthy()
    {
        // Arrange
        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Exception.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckHealthAsync_DoesNotIncludeExceptionWhenUnhealthy()
    {
        // Arrange
        _mockDaprClient
            .Setup(x => x.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "dapr",
                _sut,
                HealthStatus.Unhealthy,
                null)
        };

        // Act
        var result = await _sut.CheckHealthAsync(context);

        // Assert
        result.Exception.Should().BeNull("unhealthy status doesn't mean an exception occurred");
    }
}
