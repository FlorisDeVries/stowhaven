using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace FlorisDeV.Logging.Middleware;

/// <summary>
/// Middleware that enriches logs with authenticated user context.
/// Captures user identity information after authentication for all subsequent logs in the request.
/// </summary>
public class UserContextEnrichmentMiddleware(
    RequestDelegate next
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Check if user is authenticated
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirst("oid")?.Value
                         ?? context.User.FindFirst("sub")?.Value
                         ?? "unknown";

            var userName = context.User.Identity.Name ?? "unknown";

            // Get user roles if present
            var roles = context.User.Claims
                .Where(c => c.Type == "roles" || c.Type == "role")
                .Select(c => c.Value)
                .ToList();

            // Enrich logs with user context
            using (LogContext.PushProperty("UserId", userId))
            using (LogContext.PushProperty("UserName", userName))
            using (LogContext.PushProperty("UserRoles", roles, destructureObjects: true))
            {
                await next(context);
            }
        }
        else
        {
            // For unauthenticated requests, still mark as anonymous
            using (LogContext.PushProperty("UserId", "anonymous"))
            {
                await next(context);
            }
        }
    }
}

/// <summary>
/// Extension methods for registering user context enrichment middleware.
/// </summary>
public static class UserContextEnrichmentMiddlewareExtensions
{
    /// <summary>
    /// Adds user context enrichment middleware to enrich logs with authenticated user information.
    /// Should be registered after authentication middleware.
    /// </summary>
    public static IApplicationBuilder UseUserContextEnrichment(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<UserContextEnrichmentMiddleware>();
    }
}