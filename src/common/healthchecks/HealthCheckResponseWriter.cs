using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlorisDeV.HealthChecks;

/// <summary>
/// Custom response writer for health check endpoints that provides detailed JSON responses
/// </summary>
public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Writes a detailed JSON response for health check results
    /// </summary>
    public static Task WriteDetailedResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var response = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                duration = entry.Value.Duration.TotalMilliseconds,
                exception = entry.Value.Exception?.Message,
                data = entry.Value.Data.Count > 0 ? entry.Value.Data : null
            })
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        return context.Response.WriteAsync(json, Encoding.UTF8);
    }

    /// <summary>
    /// Writes a simple status-only response for health check results
    /// </summary>
    public static Task WriteSimpleResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var response = new
        {
            status = report.Status.ToString()
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        return context.Response.WriteAsync(json, Encoding.UTF8);
    }
}
