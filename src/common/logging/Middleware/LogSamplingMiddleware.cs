using System.Collections.Concurrent;
using FlorisDeV.Logging.Filtering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace FlorisDeV.Logging.Middleware;

/// <summary>
/// Middleware that applies sampling to logs for high-volume endpoints.
/// Uses a request counter per path pattern to determine if a request should be logged.
/// </summary>
public class LogSamplingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LogSamplingMiddleware> _logger;
    private readonly LogSamplingOptions _options;
    private readonly ConcurrentDictionary<string, long> _requestCounters = new();

    public LogSamplingMiddleware(
        RequestDelegate next,
        ILogger<LogSamplingMiddleware> logger,
        IOptions<LogSamplingOptions> options)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var shouldSample = ShouldSampleRequest(path);

        // Add sampling decision to log context
        using (LogContext.PushProperty("ShouldSample", shouldSample))
        using (LogContext.PushProperty("IsSampledEndpoint", !shouldSample))
        {
            await _next(context);
        }
    }

    private bool ShouldSampleRequest(string path)
    {
        // Find matching pattern
        foreach (var (pattern, sampleRate) in _options.PathSamplingRates)
        {
            if (IsMatch(path, pattern))
            {
                // Get or create counter for this pattern
                var counter = _requestCounters.AddOrUpdate(
                    pattern,
                    1,
                    (_, count) => Interlocked.Increment(ref count));

                // Sample based on rate (log every Nth request)
                var shouldLog = counter % sampleRate == 0;

                if (!shouldLog && counter % (sampleRate * 10) == 0)
                {
                    // Every 10x the sample rate, log a summary
                    _logger.LogDebug(
                        "Sampled {SampleRate} requests for path pattern {Pattern} (total: {TotalRequests})",
                        sampleRate, pattern, counter);
                }

                return shouldLog;
            }
        }

        // No pattern matched, log everything
        return true;
    }

    private static bool IsMatch(string path, string pattern)
    {
        // Exact match
        if (pattern.Equals(path, StringComparison.OrdinalIgnoreCase))
            return true;

        // Wildcard pattern (e.g., /health/*)
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1]; // Remove the *
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

/// <summary>
/// Extension methods for registering log sampling middleware.
/// </summary>
public static class LogSamplingMiddlewareExtensions
{
    /// <summary>
    /// Adds log sampling middleware to reduce log volume from high-frequency endpoints.
    /// Should be registered early in the pipeline after correlation ID.
    /// </summary>
    public static IApplicationBuilder UseLogSampling(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<LogSamplingMiddleware>();
    }
}
