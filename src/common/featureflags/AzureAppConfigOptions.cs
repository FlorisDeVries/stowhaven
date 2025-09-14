namespace FlorisDeV.FeatureFlags;

public class AzureAppConfigOptions
{
    public const string SectionName = "AzureAppConfig";

    /// <summary>
    ///    The connection string or the resource uri.
    /// </summary>
    /// <remarks>
    ///   Use an uri (e.g. https://app-settings.azconfig.io) to connect to the resource using managed identity (AD authentication).
    ///   Use the connection string (e.g. Endpoint=https://app-settings.azconfig.io;Secret=NQ...) when running without a managed identity.
    /// </remarks>
    public string? ConnectionEndpoint { get; set; }

    /// <summary>
    /// Trims the provided prefix from the keys of all key-values retrieved from Azure App Configuration.
    /// </summary>
    /// <example>Kae:</example>
    public string? TrimKeyPrefix { get; set; }

    public string? EnvironmentName { get; set; }

    public TimeSpan FeaturesLifetime { get; set; }

    public TimeSpan RefreshInterval { get; set; }


    public string? SentinelKey { get; set; }
}