using System.Net;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Models.Api.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Filters;

/// <summary>
/// Global exception filter that maps domain exceptions to appropriate HTTP status codes
/// and formats them as ProblemDetails responses.
/// </summary>
public class GlobalExceptionFilter(
    ILogger<GlobalExceptionFilter> logger,
    IHostEnvironment environment
) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        // Map exception to status code and problem details
        var (statusCode, problemDetails) = context.Exception switch
        {
            BackupRunNotFoundException notFoundEx => MapNotFoundException(notFoundEx, context),
            BackupRunAlreadyCommittedException alreadyCommittedEx => MapAlreadyCommittedException(alreadyCommittedEx,
                context),
            ConcurrentUpdateException concurrencyEx => MapConcurrentUpdateException(concurrencyEx, context),
            InvalidBackupRunStateException invalidStateEx => MapInvalidStateException(invalidStateEx, context),
            ArgumentNullException argNullEx => MapArgumentNullException(argNullEx, context),
            ArgumentException argEx => MapArgumentException(argEx, context),
            SecretNotFoundException secretNotFoundEx => MapSecretNotFoundException(secretNotFoundEx, context),
            SecretStoreUnavailableException secretStoreUnavailableEx => MapSecretStoreUnavailableException(
                secretStoreUnavailableEx, context),
            _ => MapUnhandledException(context.Exception, context)
        };

        // Log the exception with appropriate level
        LogException(context.Exception, statusCode);

        // Create the result
        context.Result = new ObjectResult(problemDetails)
        {
            StatusCode = statusCode
        };

        context.ExceptionHandled = true;
    }

    private (int statusCode, ProblemDetails problemDetails) MapNotFoundException(
        BackupRunNotFoundException exception,
        ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status404NotFound,
            "Backup run not found",
            exception.Message
        );

        problemDetails.Extensions["deviceId"] = exception.DeviceId;
        problemDetails.Extensions["runId"] = exception.RunId;

        return (StatusCodes.Status404NotFound, problemDetails);
    }

    private (int statusCode, ProblemDetails problemDetails) MapAlreadyCommittedException(
        BackupRunAlreadyCommittedException exception,
        ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status409Conflict,
            "Backup run already committed",
            exception.Message
        );

        problemDetails.Extensions["deviceId"] = exception.DeviceId;
        problemDetails.Extensions["runId"] = exception.RunId;

        return (StatusCodes.Status409Conflict, problemDetails);
    }

    private (int statusCode, ProblemDetails problemDetails) MapConcurrentUpdateException(
        ConcurrentUpdateException exception,
        ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status409Conflict,
            "Concurrent update conflict",
            exception.Message
        );

        problemDetails.Extensions["deviceId"] = exception.DeviceId;
        problemDetails.Extensions["runId"] = exception.RunId;

        if (!string.IsNullOrEmpty(exception.ExpectedETag))
            problemDetails.Extensions["expectedETag"] = exception.ExpectedETag;

        if (!string.IsNullOrEmpty(exception.ActualETag))
            problemDetails.Extensions["actualETag"] = exception.ActualETag;

        return (StatusCodes.Status409Conflict, problemDetails);
    }

    private (int statusCode, ProblemDetails problemDetails) MapInvalidStateException(
        InvalidBackupRunStateException exception,
        ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "Invalid backup run state",
            exception.Message
        );

        problemDetails.Extensions["deviceId"] = exception.DeviceId;
        problemDetails.Extensions["runId"] = exception.RunId;
        problemDetails.Extensions["currentStatus"] = exception.CurrentStatus.ToString();
        problemDetails.Extensions["expectedStatus"] = exception.ExpectedStatus.ToString();

        return (StatusCodes.Status422UnprocessableEntity, problemDetails);
    }

    private (int statusCode, ProblemDetails problemDetails) MapArgumentException(
        ArgumentException exception,
        ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status400BadRequest,
            "Invalid argument",
            exception.Message
        );

        if (!string.IsNullOrEmpty(exception.ParamName))
            problemDetails.Extensions["paramName"] = exception.ParamName;

        return (StatusCodes.Status400BadRequest, problemDetails);
    }

    private (int statusCode, ProblemDetails problemDetails) MapArgumentNullException(
        ArgumentNullException exception,
        ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status400BadRequest,
            "Required argument is null",
            exception.Message
        );

        if (!string.IsNullOrEmpty(exception.ParamName))
            problemDetails.Extensions["paramName"] = exception.ParamName;

        return (StatusCodes.Status400BadRequest, problemDetails);
    }

    private (int statusCode, ProblemDetails problemDetails) MapUnhandledException(
        Exception exception,
        ExceptionContext context)
    {
        var detail = environment.IsDevelopment()
            ? exception.Message
            : "An unexpected error occurred. Please try again later.";

        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status500InternalServerError,
            "Internal server error",
            detail
        );

        // Include stack trace in development mode
        if (environment.IsDevelopment())
        {
            problemDetails.Extensions["stackTrace"] = exception.StackTrace;
            problemDetails.Extensions["exceptionType"] = exception.GetType().Name;
        }

        return (StatusCodes.Status500InternalServerError, problemDetails);
    }

    private (int statusCode, ProblemDetails problemDetails) MapSecretNotFoundException(
        SecretNotFoundException secretNotFoundEx, ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status500InternalServerError,
            "Secret store unavailable",
            secretNotFoundEx.Message
        );

        problemDetails.Extensions["secretStore"] = secretNotFoundEx.SecretStore;
        problemDetails.Extensions["secretName"] = secretNotFoundEx.SecretName;

        return (StatusCodes.Status500InternalServerError, problemDetails);
    }

    private (int statusCode, ProblemDetails problemDetails) MapSecretStoreUnavailableException(
        SecretStoreUnavailableException secretStoreUnavailableEx,
        ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "Secret store unavailable",
            secretStoreUnavailableEx.Message
        );

        problemDetails.Extensions["secretStore"] = secretStoreUnavailableEx.SecretStore;

        return (StatusCodes.Status503ServiceUnavailable, problemDetails);
    }

    private ProblemDetails CreateProblemDetails(
        ExceptionContext context,
        int statusCode,
        string title,
        string detail)
    {
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.HttpContext.Request.Path,
            Type = $"https://httpstatuses.com/{statusCode}"
        };

        // Add trace ID for correlation
        problemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

        return problemDetails;
    }

    private void LogException(Exception exception, int statusCode)
    {
        var logLevel = statusCode switch
        {
            >= 500 => LogLevel.Error,
            >= 400 => LogLevel.Warning,
            _ => LogLevel.Information
        };

        logger.Log(
            logLevel,
            exception,
            "Exception handled by GlobalExceptionFilter: {ExceptionType} - {Message}",
            exception.GetType().Name,
            exception.Message
        );
    }
}