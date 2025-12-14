using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Filters;

/// <summary>
/// Base class for exception handlers providing common functionality.
/// </summary>
public abstract class ExceptionHandlerBase : IExceptionHandler
{
    public abstract bool CanHandle(Exception exception);

    public abstract (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context);

    /// <summary>
    /// Creates a ProblemDetails response with common properties populated.
    /// </summary>
    protected ProblemDetails CreateProblemDetails(
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
            Type = $"https://httpstatuses.com/{statusCode}",
            Extensions =
            {
                // Add trace ID for correlation
                ["traceId"] = context.HttpContext.TraceIdentifier
            }
        };

        return problemDetails;
    }
}
