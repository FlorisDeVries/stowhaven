using System.Diagnostics;
using FluentAssertions;
using FlorisDeV.Logging.ErrorHandling;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace FlorisDeV.Logging.Tests;

/// <summary>
/// Tests for ProblemDetailsResultFilter to verify TraceId injection into ProblemDetails responses.
/// </summary>
public class ProblemDetailsResultFilterTests
{
    private readonly ProblemDetailsResultFilter _sut;

    public ProblemDetailsResultFilterTests()
    {
        _sut = new ProblemDetailsResultFilter();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_WithProblemDetails_AddsTraceIdFromActivity()
    {
        // Arrange
        using var activity = new Activity("test-activity").Start();
        var traceId = activity.Id;

        var problemDetails = new ProblemDetails
        {
            Status = 400,
            Title = "Bad Request"
        };

        var context = CreateResultExecutingContext(problemDetails);

        // Act
        _sut.OnResultExecuting(context);

        // Assert
        problemDetails.Extensions.Should().ContainKey("traceId");
        problemDetails.Extensions["traceId"].Should().Be(traceId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_WithProblemDetails_FallsBackToHttpContextTraceIdentifier()
    {
        // Arrange - ensure no Activity.Current
        Activity.Current = null;

        var problemDetails = new ProblemDetails
        {
            Status = 500,
            Title = "Internal Server Error"
        };

        var context = CreateResultExecutingContext(problemDetails);
        var expectedTraceId = context.HttpContext.TraceIdentifier;

        // Act
        _sut.OnResultExecuting(context);

        // Assert
        problemDetails.Extensions.Should().ContainKey("traceId");
        problemDetails.Extensions["traceId"].Should().Be(expectedTraceId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_WithValidationProblemDetails_AddsTraceId()
    {
        // Arrange
        using var activity = new Activity("test-activity").Start();

        var validationProblemDetails = new ValidationProblemDetails
        {
            Status = 400,
            Title = "Validation Failed"
        };
        validationProblemDetails.Errors.Add("Field", new[] { "Required" });

        var context = CreateResultExecutingContext(validationProblemDetails);

        // Act
        _sut.OnResultExecuting(context);

        // Assert
        validationProblemDetails.Extensions.Should().ContainKey("traceId");
        var traceId = validationProblemDetails.Extensions["traceId"] as string;
        traceId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_WithNonProblemDetailsResult_DoesNotModify()
    {
        // Arrange
        var plainObject = new { message = "Success" };
        var context = CreateResultExecutingContext(plainObject);

        // Act
        var act = () => _sut.OnResultExecuting(context);

        // Assert
        act.Should().NotThrow();
        // Can't assert on plainObject.Extensions as it doesn't have that property
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_WithNonObjectResult_DoesNotThrow()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(
            actionContext,
            Array.Empty<IFilterMetadata>(),
            new StatusCodeResult(200),
            new object());

        // Act
        var act = () => _sut.OnResultExecuting(context);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_WithExistingTraceId_DoesNotOverwrite()
    {
        // Arrange
        var problemDetails = new ProblemDetails
        {
            Status = 404,
            Title = "Not Found"
        };
        
        var existingTraceId = "existing-trace-id-123";
        problemDetails.Extensions.Add("traceId", existingTraceId);

        var context = CreateResultExecutingContext(problemDetails);

        // Act
        _sut.OnResultExecuting(context);

        // Assert
        problemDetails.Extensions["traceId"].Should().Be(existingTraceId,
            "existing traceId should not be overwritten");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_WithOtherExtensions_PreservesAll()
    {
        // Arrange
        var problemDetails = new ProblemDetails
        {
            Status = 400,
            Title = "Bad Request"
        };
        problemDetails.Extensions.Add("customData", "value1");
        problemDetails.Extensions.Add("errorCode", "ERR_001");

        var context = CreateResultExecutingContext(problemDetails);

        // Act
        _sut.OnResultExecuting(context);

        // Assert
        problemDetails.Extensions.Should().HaveCount(3);
        problemDetails.Extensions["customData"].Should().Be("value1");
        problemDetails.Extensions["errorCode"].Should().Be("ERR_001");
        problemDetails.Extensions.Should().ContainKey("traceId");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuted_DoesNothing()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ResultExecutedContext(
            actionContext,
            Array.Empty<IFilterMetadata>(),
            new ObjectResult(new { }),
            new object());

        // Act
        var act = () => _sut.OnResultExecuted(context);

        // Assert
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(400, "Bad Request")]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "Not Found")]
    [InlineData(500, "Internal Server Error")]
    [InlineData(503, "Service Unavailable")]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_WithVariousStatusCodes_AddsTraceId(int statusCode, string title)
    {
        // Arrange
        using var activity = new Activity("test-activity").Start();

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title
        };

        var context = CreateResultExecutingContext(problemDetails);

        // Act
        _sut.OnResultExecuting(context);

        // Assert
        problemDetails.Extensions.Should().ContainKey("traceId");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_WithNullObjectValue_DoesNotThrow()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(
            actionContext,
            Array.Empty<IFilterMetadata>(),
            new ObjectResult(null),
            new object());

        // Act
        var act = () => _sut.OnResultExecuting(context);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnResultExecuting_UsesActivityIdFormat()
    {
        // Arrange
        using var activity = new Activity("test-activity").Start();

        var problemDetails = new ProblemDetails { Status = 500 };
        var context = CreateResultExecutingContext(problemDetails);

        // Act
        _sut.OnResultExecuting(context);

        // Assert
        var traceId = problemDetails.Extensions["traceId"] as string;
        traceId.Should().NotBeNullOrEmpty();
        // Activity.Id format is typically 00-{traceId}-{spanId}-{flags}
        traceId.Should().Match(activity.Id);
    }

    private static ResultExecutingContext CreateResultExecutingContext(object resultValue)
    {
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = Guid.NewGuid().ToString()
        };

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        return new ResultExecutingContext(
            actionContext,
            Array.Empty<IFilterMetadata>(),
            new ObjectResult(resultValue),
            new object());
    }
}
