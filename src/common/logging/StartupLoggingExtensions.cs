using System.Diagnostics;
using System.Reflection;
using FlorisDeV.Logging.Filtering;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.Logging;

/// <summary>
/// Extension methods for logging application startup configuration.
/// Helps with troubleshooting production issues by providing visibility into runtime configuration.
/// </summary>
public static class StartupLoggingExtensions
{
#pragma warning disable S1075
    private const string DefaultAspUrl = "http://localhost:5000";
#pragma warning restore S1075

    /// <summary>
    /// Logs important startup configuration information.
    /// Does NOT log secrets or sensitive information.
    /// </summary>
    public static void LogStartupConfiguration(this WebApplication app)
    {
        var logger = app.Logger;
        var configuration = app.Configuration;
        var environment = app.Environment;

        try
        {
            logger.LogInformation("========================================");
            logger.LogInformation("Application Startup Configuration");
            logger.LogInformation("========================================");

            // Application Info
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion ?? "unknown";

            logger.LogInformation("Application: {ApplicationName}", environment.ApplicationName);
            logger.LogInformation("Version: {Version}", version);
            logger.LogInformation("Environment: {EnvironmentName}", environment.EnvironmentName);
            logger.LogInformation("Content Root: {ContentRoot}", environment.ContentRootPath);
            logger.LogInformation(".NET Runtime: {RuntimeVersion}", Environment.Version);
            logger.LogInformation("OS: {OS}", Environment.OSVersion);

            // OpenTelemetry Configuration
            var otelServiceName = configuration.GetValue<string>("OTEL_SERVICE_NAME");
            var zipkinEndpoint = configuration.GetValue<string>("OTEL_EXPORTER_ZIPKIN_ENDPOINT");
            var azureMonitorConnection = configuration.GetValue<string>("OTEL_EXPORTER_AZURE_MONITOR_CONNECTION");

            logger.LogInformation("OpenTelemetry Service: {ServiceName}",
                otelServiceName ?? environment.ApplicationName);
            logger.LogInformation("OpenTelemetry Zipkin: {IsConfigured}", !string.IsNullOrEmpty(zipkinEndpoint));
            logger.LogInformation("OpenTelemetry Azure Monitor: {IsConfigured}",
                !string.IsNullOrEmpty(azureMonitorConnection));

            // Application Insights
            var appInsightsConnectionString = configuration.GetValue<string>("ApplicationInsights:ConnectionString");
            logger.LogInformation("Application Insights: {IsConfigured}",
                !string.IsNullOrEmpty(appInsightsConnectionString));

            // Authentication
            var azureAdTenantId = configuration.GetValue<string>("AzureAd:TenantId");
            var azureAdClientId = configuration.GetValue<string>("AzureAd:ClientId");

            if (environment.IsDevelopment())
            {
                logger.LogWarning("Authentication: Development mode - Anonymous authentication enabled (INSECURE)");
            }
            else
            {
                logger.LogInformation("Authentication: Azure AD JWT Bearer");
                logger.LogInformation("Azure AD Tenant: {TenantId}", MaskValue(azureAdTenantId, 8));
                logger.LogInformation("Azure AD Client: {ClientId}", MaskValue(azureAdClientId, 8));
            }

            // Dapr
            var daprHttpPort = configuration.GetValue<string>("DAPR_HTTP_PORT");
            var daprGrpcPort = configuration.GetValue<string>("DAPR_GRPC_PORT");
            logger.LogInformation("Dapr Sidecar: {IsConfigured} (HTTP: {HttpPort}, gRPC: {GrpcPort})",
                !string.IsNullOrEmpty(daprHttpPort) || !string.IsNullOrEmpty(daprGrpcPort),
                daprHttpPort ?? "default",
                daprGrpcPort ?? "default");

            // Feature Flags
            var appConfigEndpoint = configuration.GetValue<string>("AzureAppConfig:Endpoint");
            logger.LogInformation("Azure App Configuration: {IsConfigured}", !string.IsNullOrEmpty(appConfigEndpoint));

            // Log Sampling
            var samplingOptions = app.Services.GetService<IOptions<LogSamplingOptions>>()?.Value;
            if (samplingOptions != null)
            {
                logger.LogInformation("Log Sampling: {IsEnabled} (Paths: {PathCount})",
                    samplingOptions.Enabled,
                    samplingOptions.PathSamplingRates.Count);

                if (samplingOptions.Enabled)
                {
                    foreach (var (path, rate) in samplingOptions.PathSamplingRates.Take(3))
                    {
                        logger.LogInformation("  - {Path}: 1/{Rate}", path, rate);
                    }
                }
            }

            // URLs
            var urls = configuration.GetValue<string>("ASPNETCORE_URLS")
                       ?? configuration.GetValue<string>("urls")
                       ?? DefaultAspUrl;
            logger.LogInformation("Listening on: {Urls}", urls);

            // Kestrel Configuration
            var keepAliveTimeout = configuration.GetValue<int?>("Kestrel:Limits:KeepAliveTimeout");
            if (keepAliveTimeout.HasValue)
            {
                logger.LogInformation("Kestrel KeepAlive Timeout: {Timeout}s", keepAliveTimeout.Value);
            }

            logger.LogInformation("========================================");
            logger.LogInformation("Configuration logging complete. Starting application...");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log complete startup configuration");
        }
    }

    /// <summary>
    /// Logs important startup configuration information for console applications.
    /// Does NOT log secrets or sensitive information.
    /// </summary>
    public static void LogStartupConfiguration(this IHost host)
    {
        var loggerFactory = host.Services.GetService<ILoggerFactory>();
        if (loggerFactory == null) return;

        var logger = loggerFactory.CreateLogger("Startup");
        var configuration = host.Services.GetService<IConfiguration>();

        if (configuration == null) return;

        try
        {
            logger.LogInformation("========================================");
            logger.LogInformation("Application Startup Configuration");
            logger.LogInformation("========================================");

            // Application Info
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion ?? "unknown";
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                              ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? "Production";

            logger.LogInformation("Application: {ApplicationName}", assembly.GetName().Name);
            logger.LogInformation("Version: {Version}", version);
            logger.LogInformation("Environment: {EnvironmentName}", environment);
            logger.LogInformation(".NET Runtime: {RuntimeVersion}", Environment.Version);
            logger.LogInformation("OS: {OS}", Environment.OSVersion);

            // OpenTelemetry Configuration
            var otelServiceName = configuration.GetValue<string>("OTEL_SERVICE_NAME");
            var otlpEndpoint = configuration.GetValue<string>("OTEL_EXPORTER_OTLP_ENDPOINT");
            var zipkinEndpoint = configuration.GetValue<string>("OTEL_EXPORTER_ZIPKIN_ENDPOINT");
            var azureMonitorConnection = configuration.GetValue<string>("OTEL_EXPORTER_AZURE_MONITOR_CONNECTION");

            logger.LogInformation("OpenTelemetry Service: {ServiceName}", otelServiceName ?? assembly.GetName().Name);
            logger.LogInformation("OpenTelemetry OTLP: {IsConfigured}", !string.IsNullOrEmpty(otlpEndpoint));
            logger.LogInformation("OpenTelemetry Zipkin: {IsConfigured}", !string.IsNullOrEmpty(zipkinEndpoint));
            logger.LogInformation("OpenTelemetry Azure Monitor: {IsConfigured}",
                !string.IsNullOrEmpty(azureMonitorConnection));

            logger.LogInformation("========================================");
            logger.LogInformation("Configuration logging complete. Starting application...");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log complete startup configuration");
        }
    }

    /// <summary>
    /// Masks a value to show only the last N characters (for logging IDs without exposing full value).
    /// </summary>
    private static string MaskValue(string? value, int showLast = 4)
    {
        if (string.IsNullOrEmpty(value))
            return "not configured";

        if (value.Length <= showLast)
            return value;

        return "..." + value[^showLast..];
    }
}