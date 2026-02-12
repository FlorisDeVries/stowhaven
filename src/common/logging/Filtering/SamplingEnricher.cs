using Serilog.Core;
using Serilog.Events;

namespace FlorisDeV.Logging.Filtering;

/// <summary>
/// Serilog enricher that filters out sampled logs based on the ShouldSample property.
/// Works in conjunction with LogSamplingMiddleware.
/// </summary>
public class SamplingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
    }
}

/// <summary>
/// Serilog filter that drops log events marked as sampled (ShouldSample = false).
/// Only applies to configured log levels to ensure warnings/errors are never dropped.
/// </summary>
public class LogSamplingFilter
{
    private readonly LogSamplingOptions _options;

    public LogSamplingFilter(LogSamplingOptions options)
    {
        _options = options;
    }

    public bool IsEnabled(LogEvent logEvent)
    {
        if (!_options.Enabled)
            return true;

        // Always log Warning, Error, Fatal - never sample these
        if (logEvent.Level >= LogEventLevel.Warning)
            return true;

        // Check if this log level should be sampled
        var levelName = logEvent.Level.ToString();
        if (!_options.SampledLogLevels.Contains(levelName, StringComparer.OrdinalIgnoreCase))
            return true;

        // Check if ShouldSample property exists and is false
        if (logEvent.Properties.TryGetValue("ShouldSample", out var shouldSampleProperty) &&
            shouldSampleProperty is ScalarValue { Value: bool shouldSample })
        {
            return shouldSample;
        }

        // If IsSampledEndpoint is true and ShouldSample is not set, it means it was sampled out
        if (logEvent.Properties.TryGetValue("IsSampledEndpoint", out var isSampledProperty) &&
            isSampledProperty is ScalarValue { Value: true })
        {
            return false; // Drop this log
        }

        // Default: allow the log
        return true;
    }
}