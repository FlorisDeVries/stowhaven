using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Filters;
using FlorisDeV.BackupApi.Models.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public class GlobalExceptionFilterTests
{
    private readonly Mock<ILogger<GlobalExceptionFilter>> _loggerMock;
    private readonly Mock<IHostEnvironment> _environmentMock;
    private readonly GlobalExceptionFilter _filter;

    public GlobalExceptionFilterTests()
    {
        _loggerMock = new Mock<ILogger<GlobalExceptionFilter>>();
        _environmentMock = new Mock<IHostEnvironment>();
        
        // Create all handlers
        var handlers = new List<IExceptionHandler>
        {
            new BackupRunNotFoundExceptionHandler(),
            new BackupRunAlreadyCommittedExceptionHandler(),
            new ConcurrentUpdateExceptionHandler(),
            new InvalidBackupRunStateExceptionHandler(),
            new SecretNotFoundExceptionHandler(),
            new SecretStoreUnavailableExceptionHandler(),
            new ArgumentNullExceptionHandler(),
            new ArgumentExceptionHandler(),
            new UnhandledExceptionHandler(_environmentMock.Object)
        };
        
        _filter = new GlobalExceptionFilter(_loggerMock.Object, handlers);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnException_WithBackupRunNotFoundException_Returns404()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var exception = new BackupRunNotFoundException(deviceId, runId);
        var context = CreateExceptionContext(exception);

        // Act
        _filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(404, result.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Backup run not found", problemDetails.Title);
        Assert.Equal(404, problemDetails.Status);
        Assert.Contains(deviceId.ToString(), problemDetails.Extensions["deviceId"]?.ToString());
        Assert.Contains(runId.ToString(), problemDetails.Extensions["runId"]?.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnException_WithBackupRunAlreadyCommittedException_Returns409()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var exception = new BackupRunAlreadyCommittedException(deviceId, runId);
        var context = CreateExceptionContext(exception);

        // Act
        _filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(409, result.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Backup run already committed", problemDetails.Title);
        Assert.Equal(409, problemDetails.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnException_WithConcurrentUpdateException_Returns409()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var expectedETag = "v1";
        var exception = new ConcurrentUpdateException(deviceId, runId, expectedETag, actualETag: null);
        var context = CreateExceptionContext(exception);

        // Act
        _filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(409, result.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Concurrent update conflict", problemDetails.Title);
        Assert.Equal(409, problemDetails.Status);
        Assert.Equal(expectedETag, problemDetails.Extensions["expectedETag"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnException_WithInvalidBackupRunStateException_Returns422()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var exception = new InvalidBackupRunStateException(
            deviceId,
            runId,
            BackupRunStatus.Failed,
            BackupRunStatus.Queued);
        var context = CreateExceptionContext(exception);

        // Act
        _filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(422, result.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Invalid backup run state", problemDetails.Title);
        Assert.Equal(422, problemDetails.Status);
        Assert.Equal("Failed", problemDetails.Extensions["currentStatus"]);
        Assert.Equal("Queued", problemDetails.Extensions["expectedStatus"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnException_WithArgumentException_Returns400()
    {
        // Arrange
        var exception = new ArgumentException("Invalid device ID", "deviceId");
        var context = CreateExceptionContext(exception);

        // Act
        _filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(400, result.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Invalid argument", problemDetails.Title);
        Assert.Equal("deviceId", problemDetails.Extensions["paramName"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnException_WithArgumentNullException_Returns400()
    {
        // Arrange
        var exception = new ArgumentNullException("request");
        var context = CreateExceptionContext(exception);

        // Act
        _filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(400, result.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Required argument is null", problemDetails.Title);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnException_WithUnhandledException_Returns500()
    {
        // Arrange
        var exception = new InvalidOperationException("Something went wrong");
        var context = CreateExceptionContext(exception);
        _environmentMock.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);

        // Act
        _filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(500, result.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Internal server error", problemDetails.Title);
        Assert.Equal(500, problemDetails.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnException_InDevelopmentMode_IncludesStackTrace()
    {
        // Arrange
        var exception = new InvalidOperationException("Something went wrong");
        var context = CreateExceptionContext(exception);
        _environmentMock.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);

        // Act
        _filter.OnException(context);

        // Assert
        var result = Assert.IsType<ObjectResult>(context.Result);
        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);

        Assert.Contains("stackTrace", problemDetails.Extensions.Keys);
        Assert.Contains("exceptionType", problemDetails.Extensions.Keys);
        Assert.Equal("InvalidOperationException", problemDetails.Extensions["exceptionType"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OnException_AlwaysIncludesTraceId()
    {
        // Arrange
        var exception = new Exception("Test exception");
        var context = CreateExceptionContext(exception);
        var expectedTraceId = "test-trace-id-123";
        context.HttpContext.TraceIdentifier = expectedTraceId;

        // Act
        _filter.OnException(context);

        // Assert
        var result = Assert.IsType<ObjectResult>(context.Result);
        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);

        Assert.Contains("traceId", problemDetails.Extensions.Keys);
        Assert.Equal(expectedTraceId, problemDetails.Extensions["traceId"]);
    }

    private ExceptionContext CreateExceptionContext(Exception exception)
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor()
        );

        return new ExceptionContext(actionContext, new List<IFilterMetadata>())
        {
            Exception = exception
        };
    }
}
