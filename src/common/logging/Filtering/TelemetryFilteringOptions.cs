namespace FlorisDeV.Logging.Filtering;

public class TelemetryFilteringOptions
{
    public const string SectionName = "TelemetryFilters";

    /// <summary>
    ///   Request operations to be filtered out
    /// </summary>
    /// <example>GET /dapr/*</example>
    public ISet<string>? IgnoreRequests { get; set; }

    /// <summary>
    ///   Dependency urls to be filtered out
    /// </summary>
    /// <example>http://127.0.0.1:*/*</example>
    public ISet<string>? IgnoreDependencies { get; set; }

    /// <summary>
    ///   Operation name to be filtered out
    /// </summary>
    /// <example>
    ///   With OpenTelemetry use 'System.Net.Http.HttpRequestOut' to filter all dependency calls,
    ///   and 'Microsoft.AspNetCore.Hosting.HttpRequestIn' to filter all incoming requests
    /// </example>
    public ISet<string>? IgnoreOperationNames { get; set; }
}