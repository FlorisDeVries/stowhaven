using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace FlorisDeV.Logging.Middleware;

/// <summary>
/// Middleware that enriches logs with correlation and request IDs from HTTP context.
/// Ensures distributed tracing context is captured in all logs for the request lifetime.
/// </summary>
public class CorrelationIdMiddleware(
    RequestDelegate next
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.TraceIdentifier;

        // Check for custom correlation ID header (e.g., from API Gateway or client)
        if (context.Request.Headers.TryGetValue("X-Correlation-ID", out var headerCorrelationId) &&
            !string.IsNullOrWhiteSpace(headerCorrelationId))
        {
            correlationId = headerCorrelationId.ToString();
        }

        // Enrich all logs within this request scope with correlation context
        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("RequestId", context.TraceIdentifier))
        using (LogContext.PushProperty("RequestPath", context.Request.Path))
        using (LogContext.PushProperty("RequestMethod", context.Request.Method))
        {
            // Add correlation ID to response headers for client tracking
            context.Response.OnStarting(() =>
            {
                if (!context.Response.Headers.ContainsKey("X-Correlation-ID"))
                {
                    context.Response.Headers.Append("X-Correlation-ID", correlationId);
                }

                return Task.CompletedTask;
            });

            await next(context);
        }
    }
}

/// <summary>
/// Extension methods for registering correlation middleware.
/// </summary>
public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Adds correlation ID middleware to enrich logs with request context.
    /// Should be registered early in the pipeline, after exception handling but before authentication.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CorrelationIdMiddleware>();
    }
}