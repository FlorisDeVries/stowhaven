using System.Text;
using System.Text.Json;
using FluentAssertions;
using FlorisDeV.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlorisDeV.HealthChecks.Tests;

/// <summary>
/// Tests for HealthCheckResponseWriter to verify JSON serialization of health check results.
/// </summary>
public class HealthCheckResponseWriterTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteDetailedResponse_WithHealthyStatus_WritesCorrectJson()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["test-check"] = new HealthReportEntry(
                    HealthStatus.Healthy,
                    "Test is healthy",
                    TimeSpan.FromMilliseconds(100),
                    null,
                    new Dictionary<string, object> { ["key1"] = "value1" })
            },
            TimeSpan.FromMilliseconds(150));

        // Act
        await HealthCheckResponseWriter.WriteDetailedResponse(context, report);

        // Assert
        context.Response.ContentType.Should().Be("application/json; charset=utf-8");
        
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var json = JsonDocument.Parse(responseBody);
        json.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        json.RootElement.GetProperty("totalDuration").GetDouble().Should().Be(150);
        
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();
        checks.Should().HaveCount(1);
        checks[0].GetProperty("name").GetString().Should().Be("test-check");
        checks[0].GetProperty("status").GetString().Should().Be("Healthy");
        checks[0].GetProperty("description").GetString().Should().Be("Test is healthy");
        checks[0].GetProperty("duration").GetDouble().Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteDetailedResponse_WithUnhealthyStatus_IncludesExceptionMessage()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var exception = new InvalidOperationException("Service unavailable");
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["failing-check"] = new HealthReportEntry(
                    HealthStatus.Unhealthy,
                    "Check failed",
                    TimeSpan.FromMilliseconds(50),
                    exception,
                    null)
            },
            TimeSpan.FromMilliseconds(75));

        // Act
        await HealthCheckResponseWriter.WriteDetailedResponse(context, report);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var json = JsonDocument.Parse(responseBody);
        json.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");
        
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();
        checks[0].GetProperty("exception").GetString().Should().Be("Service unavailable");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteDetailedResponse_WithMultipleChecks_IncludesAll()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["check1"] = new HealthReportEntry(
                    HealthStatus.Healthy,
                    "First check passed",
                    TimeSpan.FromMilliseconds(10),
                    null,
                    null),
                ["check2"] = new HealthReportEntry(
                    HealthStatus.Degraded,
                    "Second check degraded",
                    TimeSpan.FromMilliseconds(20),
                    null,
                    new Dictionary<string, object> { ["warning"] = "slow response" }),
                ["check3"] = new HealthReportEntry(
                    HealthStatus.Unhealthy,
                    "Third check failed",
                    TimeSpan.FromMilliseconds(30),
                    new Exception("Failed"),
                    null)
            },
            TimeSpan.FromMilliseconds(100));

        // Act
        await HealthCheckResponseWriter.WriteDetailedResponse(context, report);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var json = JsonDocument.Parse(responseBody);
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();
        checks.Should().HaveCount(3);
        
        checks.Should().Contain(c => c.GetProperty("name").GetString() == "check1");
        checks.Should().Contain(c => c.GetProperty("name").GetString() == "check2");
        checks.Should().Contain(c => c.GetProperty("name").GetString() == "check3");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteDetailedResponse_WithNoData_ExcludesDataProperty()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["no-data-check"] = new HealthReportEntry(
                    HealthStatus.Healthy,
                    "Check without data",
                    TimeSpan.FromMilliseconds(5),
                    null,
                    null)
            },
            TimeSpan.FromMilliseconds(10));

        // Act
        await HealthCheckResponseWriter.WriteDetailedResponse(context, report);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var json = JsonDocument.Parse(responseBody);
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();
        
        // Data property should be null when there's no data
        var dataProperty = checks[0].TryGetProperty("data", out var data);
        dataProperty.Should().BeTrue();
        data.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteDetailedResponse_WithData_IncludesDataObject()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["data-check"] = new HealthReportEntry(
                    HealthStatus.Healthy,
                    "Check with data",
                    TimeSpan.FromMilliseconds(5),
                    null,
                    new Dictionary<string, object> 
                    { 
                        ["version"] = "1.0.0",
                        ["alive"] = true,
                        ["count"] = 42
                    })
            },
            TimeSpan.FromMilliseconds(10));

        // Act
        await HealthCheckResponseWriter.WriteDetailedResponse(context, report);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var json = JsonDocument.Parse(responseBody);
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();
        var data = checks[0].GetProperty("data");
        
        data.GetProperty("version").GetString().Should().Be("1.0.0");
        data.GetProperty("alive").GetBoolean().Should().BeTrue();
        data.GetProperty("count").GetInt32().Should().Be(42);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteSimpleResponse_WritesStatusOnly()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["check1"] = new HealthReportEntry(
                    HealthStatus.Healthy,
                    "Description",
                    TimeSpan.FromMilliseconds(10),
                    null,
                    new Dictionary<string, object> { ["key"] = "value" })
            },
            TimeSpan.FromMilliseconds(20));

        // Act
        await HealthCheckResponseWriter.WriteSimpleResponse(context, report);

        // Assert
        context.Response.ContentType.Should().Be("application/json; charset=utf-8");
        
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var json = JsonDocument.Parse(responseBody);
        json.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        
        // Should not include checks, duration, or other details
        json.RootElement.TryGetProperty("checks", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("totalDuration", out _).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteSimpleResponse_WithUnhealthyStatus_WritesCorrectStatus()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["failing"] = new HealthReportEntry(
                    HealthStatus.Unhealthy,
                    "Failed",
                    TimeSpan.FromMilliseconds(10),
                    new Exception("Error"),
                    null)
            },
            TimeSpan.FromMilliseconds(20));

        // Act
        await HealthCheckResponseWriter.WriteSimpleResponse(context, report);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var json = JsonDocument.Parse(responseBody);
        json.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteSimpleResponse_WithDegradedStatus_WritesCorrectStatus()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["degraded"] = new HealthReportEntry(
                    HealthStatus.Degraded,
                    "Degraded",
                    TimeSpan.FromMilliseconds(10),
                    null,
                    null)
            },
            TimeSpan.FromMilliseconds(20));

        // Act
        await HealthCheckResponseWriter.WriteSimpleResponse(context, report);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var json = JsonDocument.Parse(responseBody);
        json.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteDetailedResponse_ProducesValidJson()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["api"] = new HealthReportEntry(HealthStatus.Healthy, "OK", TimeSpan.FromMilliseconds(5), null, null)
            },
            TimeSpan.FromMilliseconds(10));

        // Act
        await HealthCheckResponseWriter.WriteDetailedResponse(context, report);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var act = () => JsonDocument.Parse(responseBody);
        act.Should().NotThrow("response should be valid JSON");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WriteSimpleResponse_ProducesValidJson()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            TimeSpan.FromMilliseconds(0));

        // Act
        await HealthCheckResponseWriter.WriteSimpleResponse(context, report);

        // Assert
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        var act = () => JsonDocument.Parse(responseBody);
        act.Should().NotThrow("response should be valid JSON");
    }
}
