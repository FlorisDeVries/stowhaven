namespace FlorisDeV.Logging;

/// <summary>
///   Defines OpenTelemetry resource attributes for service identification and metadata.
/// </summary>
public sealed class OtelResourceAttributes
{
    /// <summary>
    ///   Logical name of the service (e.g., "backup-client").
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    ///   Version of the service instance (e.g., semantic version from assembly).
    /// </summary>
    public required string ServiceVersion { get; init; }

    /// <summary>
    ///   Deployment environment (e.g., "Development", "Production").
    /// </summary>
    public string? DeploymentEnvironment { get; init; }

    /// <summary>
    ///   Optional additional attributes to include in the resource.
    /// </summary>
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; init; }
}
