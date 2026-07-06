namespace FlorisDeV.HealthChecks;

public sealed class DaprHealthCheckOptions
{
    public const string SectionName = "DaprHealth";

    public bool EnableStateStoreProbes { get; init; } = true;
    public bool EnablePubSubProbe { get; init; } = true;

    // Application state lives in IStateDocumentStore, not in Dapr state stores,
    // so there are no stores to probe by default.
    public string[] StateStores { get; init; } = [];
    public string PubSubComponent { get; init; } = "backup-events-pubsub";
    public string PubSubTopic { get; init; } = "health-probe";
    public int TimeoutSeconds { get; init; } = 5;
}
