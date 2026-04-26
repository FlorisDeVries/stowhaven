using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.Logging.ErrorHandling;

/// <summary>
/// Global exception filter that delegates exception handling to specialized handlers.
/// Uses the chain of responsibility pattern to make exception handling extensible and maintainable.
/// </summary>
public class GlobalExceptionFilter(
    ILogger<GlobalExceptionFilter> logger,
    IEnumerable<IExceptionHandler> handlers
) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        // Find the first handler that can handle this exception
        var handler = handlers.FirstOrDefault(h => h.CanHandle(context.Exception));

        if (handler == null)
        {
            logger.LogError(
                context.Exception,
                "No exception handler found for exception type: {ExceptionType}",
                context.Exception.GetType().Name);
            return;
        }

        // Handle the exception
        var (statusCode, problemDetails) = handler.Handle(context.Exception, context);

        // Log the exception with appropriate level
        LogException(context.Exception, statusCode);

        // Create the result
        context.Result = new ObjectResult(problemDetails)
        {
            StatusCode = statusCode
        };

        context.ExceptionHandled = true;
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