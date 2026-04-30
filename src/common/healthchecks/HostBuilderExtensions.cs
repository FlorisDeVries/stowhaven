using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlorisDeV.HealthChecks;

public static class HostBuilderExtensions
{
    /// <summary>
    ///   Adds the standard health checks (self, dapr, and optionally Azure Blob Storage)
    /// </summary>
    public static IHealthChecksBuilder AddStandardHealthChecks(
        this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        services.Configure<DaprHealthCheckOptions>(builder.Configuration.GetSection(DaprHealthCheckOptions.SectionName));

        var healthChecksBuilder = services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "self", "ready" })
            .AddCheck<DaprHealthCheck>("dapr", tags: new[] { "dapr", "ready" });

        // Add Azure Blob Storage health check if BlobServiceClient is registered
        var blobServiceClient = services.BuildServiceProvider().GetService<BlobServiceClient>();
        if (blobServiceClient != null)
        {
            healthChecksBuilder.AddCheck<AzureBlobStorageHealthCheck>(
                "azure-blob-storage",
                tags: new[] { "azure", "storage", "ready" });
        }

        return healthChecksBuilder;
    }

    /// <summary>
    ///   Adds the liveness and readiness endpoints to the <see cref="IEndpointRouteBuilder"/>.
    /// </summary>
    /// <remarks>
    ///   A semi-colon delimited list with host names (e.g. *:8080) is
    ///   fetched from configuration property 'AllowedHealthCheckHosts'.
    ///   If the list is empty, then any hosts will be accepted.
    /// </remarks>
    public static WebApplication MapStandardHealthChecks(this WebApplication app)
    {
        var allowedHostString = app.Configuration.GetValue("AllowedHealthCheckHosts", string.Empty);
        var requiredHosts = allowedHostString!.Split(";", StringSplitOptions.RemoveEmptyEntries);

        // Liveness endpoint - only checks if the app is running
        var liveness = app.MapHealthChecks("/health/liveness", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("self"),
            ResponseWriter = HealthCheckResponseWriter.WriteSimpleResponse
        });

        if (requiredHosts is { Length: > 0 })
        {
            liveness.RequireHost(requiredHosts);
        }

        // Readiness endpoint - checks if the app and dependencies are ready
        var readiness = app.MapHealthChecks("/health/readiness", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = HealthCheckResponseWriter.WriteDetailedResponse
        });

        if (requiredHosts is { Length: > 0 })
        {
            readiness.RequireHost(requiredHosts);
        }

        return app;
    }
}