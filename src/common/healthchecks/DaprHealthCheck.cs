using Dapr.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlorisDeV.HealthChecks;

public class DaprHealthCheck(DaprClient daprClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var healthy = await daprClient.CheckHealthAsync(cancellationToken).ConfigureAwait(false);

        if (healthy)
        {
            return HealthCheckResult.Healthy("Dapr sidecar is healthy.");
        }

        return new HealthCheckResult(context.Registration.FailureStatus, "Dapr sidecar is unhealthy.");
    }
}