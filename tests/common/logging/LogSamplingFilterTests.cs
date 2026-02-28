using FluentAssertions;
using FlorisDeV.Logging.Filtering;
using Serilog.Events;

namespace FlorisDeV.Logging.Tests;

/// <summary>
/// Tests for LogSamplingFilter to verify log event filtering based on sampling decisions.
/// </summary>
public class LogSamplingFilterTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WhenSamplingDisabled_AlwaysReturnsTrue()
    {
        // Arrange
        var options = new LogSamplingOptions { Enabled = false };
        var filter = new LogSamplingFilter(options);

        var logEvent = CreateLogEvent(LogEventLevel.Information);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData(LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Error)]
    [InlineData(LogEventLevel.Fatal)]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithWarningOrHigher_AlwaysReturnsTrue(LogEventLevel level)
    {
        // Arrange
        var options = new LogSamplingOptions { Enabled = true };
        var filter = new LogSamplingFilter(options);

        var logEvent = CreateLogEvent(level, shouldSample: false);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeTrue("Warning, Error, and Fatal logs should never be sampled out");
    }

    [Theory]
    [InlineData(LogEventLevel.Information)]
    [InlineData(LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Verbose)]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithConfiguredLevels_RespectsSamplingDecision(LogEventLevel level)
    {
        // Arrange
        var options = new LogSamplingOptions
        {
            Enabled = true,
            SampledLogLevels = new[] { "Information", "Debug", "Verbose" }
        };
        var filter = new LogSamplingFilter(options);

        var shouldSampleTrue = CreateLogEvent(level, shouldSample: true);
        var shouldSampleFalse = CreateLogEvent(level, shouldSample: false);

        // Act & Assert
        filter.IsEnabled(shouldSampleTrue).Should().BeTrue();
        filter.IsEnabled(shouldSampleFalse).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithShouldSampleTrue_ReturnsTrue()
    {
        // Arrange
        var options = new LogSamplingOptions { Enabled = true };
        var filter = new LogSamplingFilter(options);

        var logEvent = CreateLogEvent(LogEventLevel.Information, shouldSample: true);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithShouldSampleFalse_ReturnsFalse()
    {
        // Arrange
        var options = new LogSamplingOptions { Enabled = true };
        var filter = new LogSamplingFilter(options);

        var logEvent = CreateLogEvent(LogEventLevel.Information, shouldSample: false);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithIsSampledEndpointTrue_ReturnsFalse()
    {
        // Arrange
        var options = new LogSamplingOptions { Enabled = true };
        var filter = new LogSamplingFilter(options);

        var logEvent = CreateLogEvent(LogEventLevel.Information, isSampledEndpoint: true);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeFalse("IsSampledEndpoint=true means the log should be filtered out");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithIsSampledEndpointFalse_ReturnsTrue()
    {
        // Arrange
        var options = new LogSamplingOptions { Enabled = true };
        var filter = new LogSamplingFilter(options);

        var logEvent = CreateLogEvent(LogEventLevel.Information, isSampledEndpoint: false);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithNoSamplingProperties_ReturnsTrue()
    {
        // Arrange
        var options = new LogSamplingOptions { Enabled = true };
        var filter = new LogSamplingFilter(options);

        var logEvent = CreateLogEvent(LogEventLevel.Information);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeTrue("Default behavior when no sampling properties exist");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithLevelNotInSampledList_ReturnsTrue()
    {
        // Arrange
        var options = new LogSamplingOptions
        {
            Enabled = true,
            SampledLogLevels = new[] { "Debug", "Verbose" } // Not including Information
        };
        var filter = new LogSamplingFilter(options);

        var logEvent = CreateLogEvent(LogEventLevel.Information, shouldSample: false);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeTrue("Information is not in the sampled levels list, so it shouldn't be filtered");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_IsCaseInsensitiveForLogLevels()
    {
        // Arrange
        var options = new LogSamplingOptions
        {
            Enabled = true,
            SampledLogLevels = new[] { "information", "DEBUG", "VeRbOsE" }
        };
        var filter = new LogSamplingFilter(options);

        var infoEvent = CreateLogEvent(LogEventLevel.Information, shouldSample: false);
        var debugEvent = CreateLogEvent(LogEventLevel.Debug, shouldSample: false);
        var verboseEvent = CreateLogEvent(LogEventLevel.Verbose, shouldSample: false);

        // Act & Assert
        filter.IsEnabled(infoEvent).Should().BeFalse();
        filter.IsEnabled(debugEvent).Should().BeFalse();
        filter.IsEnabled(verboseEvent).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithShouldSampleTakesPrecedenceOverIsSampledEndpoint()
    {
        // Arrange
        var options = new LogSamplingOptions { Enabled = true };
        var filter = new LogSamplingFilter(options);

        // When ShouldSample is explicitly set, it should take precedence
        var logEvent = CreateLogEvent(LogEventLevel.Information, shouldSample: true, isSampledEndpoint: true);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeTrue("ShouldSample=true should take precedence");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithDefaultOptions_FiltersSampledEndpoints()
    {
        // Arrange
        var options = new LogSamplingOptions(); // Using defaults
        var filter = new LogSamplingFilter(options);

        var regularLog = CreateLogEvent(LogEventLevel.Information);
        var sampledLog = CreateLogEvent(LogEventLevel.Information, isSampledEndpoint: true);

        // Act & Assert
        filter.IsEnabled(regularLog).Should().BeTrue();
        filter.IsEnabled(sampledLog).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_WithTraceLevel_CanBeSampled()
    {
        // Arrange
        var options = new LogSamplingOptions
        {
            Enabled = true,
            SampledLogLevels = new[] { "Verbose", "Debug", "Trace" }
        };
        var filter = new LogSamplingFilter(options);

        var logEvent = CreateLogEvent(LogEventLevel.Verbose, shouldSample: false);

        // Act
        var result = filter.IsEnabled(logEvent);

        // Assert
        result.Should().BeFalse();
    }

    private static LogEvent CreateLogEvent(
        LogEventLevel level,
        bool? shouldSample = null,
        bool? isSampledEndpoint = null)
    {
        var properties = new List<LogEventProperty>();

        if (shouldSample.HasValue)
        {
            properties.Add(new LogEventProperty("ShouldSample", new ScalarValue(shouldSample.Value)));
        }

        if (isSampledEndpoint.HasValue)
        {
            properties.Add(new LogEventProperty("IsSampledEndpoint", new ScalarValue(isSampledEndpoint.Value)));
        }

        return new LogEvent(
            DateTimeOffset.UtcNow,
            level,
            null,
            MessageTemplate.Empty,
            properties);
    }
}
