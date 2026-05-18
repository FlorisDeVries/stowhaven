using Dapr.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FlorisDeV.HealthChecks;

public class DaprHealthCheck(DaprClient daprClient, IOptions<DaprHealthCheckOptions> options) : IHealthCheck
{
    private readonly DaprHealthCheckOptions _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        var ct = timeout.Token;

        var data = new Dictionary<string, object>();

        var healthy = await daprClient.CheckHealthAsync(ct).ConfigureAwait(false);

        data["sidecar"] = healthy ? "healthy" : "unhealthy";

        if (!healthy)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Dapr sidecar is unhealthy.", data: data);
        }

        if (_options.EnableStateStoreProbes)
        {
            foreach (var stateStore in _options.StateStores.Where(static store => !string.IsNullOrWhiteSpace(store)))
            {
                await ProbeStateStoreAsync(stateStore, data, ct).ConfigureAwait(false);
            }
        }

        if (_options.EnablePubSubProbe)
        {
            await ProbePubSubAsync(data, ct).ConfigureAwait(false);
        }

        return HealthCheckResult.Healthy("Dapr sidecar and configured components are healthy.", data);
    }

    private async Task ProbeStateStoreAsync(string stateStore, Dictionary<string, object> data, CancellationToken cancellationToken)
    {
        // Cosmos DB item ids cannot contain '/', '\\', '?' or '#'. Dapr's
        // Cosmos state store uses the state key as the item id, so readiness
        // probe keys must use a Cosmos-safe separator.
        var key = $"health:readiness:{Environment.MachineName}:{Guid.NewGuid():N}";
        var value = new DaprReadinessProbe(DateTimeOffset.UtcNow);

        await daprClient.SaveStateAsync(stateStore, key, value, cancellationToken: cancellationToken).ConfigureAwait(false);
        var readBack = await daprClient.GetStateAsync<DaprReadinessProbe>(stateStore, key, cancellationToken: cancellationToken).ConfigureAwait(false);
        await daprClient.DeleteStateAsync(stateStore, key, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (readBack == null)
        {
            throw new InvalidOperationException($"Dapr state store '{stateStore}' did not return readiness probe value.");
        }

        data[$"state:{stateStore}"] = "healthy";
    }

    private async Task ProbePubSubAsync(Dictionary<string, object> data, CancellationToken cancellationToken)
    {
        await daprClient.PublishEventAsync(
            _options.PubSubComponent,
            _options.PubSubTopic,
            new DaprReadinessProbe(DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        data[$"pubsub:{_options.PubSubComponent}/{_options.PubSubTopic}"] = "healthy";
    }

    private sealed record DaprReadinessProbe(DateTimeOffset Timestamp);
}