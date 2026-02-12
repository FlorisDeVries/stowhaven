namespace FlorisDeV.Logging.Filtering;

/// <summary>
/// Configuration for log sampling to reduce log volume from high-frequency endpoints.
/// </summary>
public class LogSamplingOptions
{
    public const string SectionName = "Logging:Sampling";

    /// <summary>
    /// Enable log sampling
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path patterns and their sampling rates.
    /// Key: Path pattern (supports wildcards like /health/* or exact match /health/liveness)
    /// Value: Sampling rate (1 = log all, 10 = log every 10th request, 100 = log every 100th request)
    /// </summary>
    public Dictionary<string, int> PathSamplingRates { get; set; } = new()
    {
        { "/health/*", 20 },
        { "/healthz", 20 },
        { "/api/health/alive", 50 },
        { "/api/health/ready", 20 }
    };

    /// <summary>
    /// Log levels to apply sampling to. Higher levels (Warning, Error) are always logged.
    /// </summary>
    public string[] SampledLogLevels { get; set; } = { "Information", "Debug", "Trace" };
}
