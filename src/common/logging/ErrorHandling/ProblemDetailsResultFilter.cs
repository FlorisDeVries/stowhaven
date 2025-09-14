using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.Logging.ErrorHandling;

/// <summary>
///   Adds traceId to all api results derived from <see cref="ProblemDetails"/>.
/// </summary>
/// <remarks>
///   The filter must be registered/configured when controllers are registered as services.
/// </remarks>
/// <code>
///   services.AddControllers(options => options.Filters.Add{ProblemDetailsResultFilter}())
/// </code>
public class ProblemDetailsResultFilter : IResultFilter
{
    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult { Value: ProblemDetails problemDetails })
        {
            var traceId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            problemDetails.Extensions.TryAdd("traceId", traceId);
        }
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
        // no action
    }
}