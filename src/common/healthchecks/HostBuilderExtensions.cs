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
    ///   Adds the standard health checks (self, dapr)
    /// </summary>
    public static IHealthChecksBuilder AddStandardHealthChecks(
        this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        return services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "self" })
            .AddCheck<DaprHealthCheck>("dapr", tags: new [] { "dapr" });
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

        var liveness = app.MapHealthChecks("/health/liveness", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("self")
        });

        if (requiredHosts is { Length: > 0 })
        {
            liveness.RequireHost(requiredHosts);
        }

        var readiness = app.MapHealthChecks("/health/readiness", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("self") || r.Tags.Contains("dapr")
        });

        if (requiredHosts is { Length: > 0 })
        {
            readiness.RequireHost(requiredHosts);
        }

        return app;
    }
}