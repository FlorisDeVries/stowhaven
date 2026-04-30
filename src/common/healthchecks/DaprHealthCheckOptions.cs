namespace FlorisDeV.HealthChecks;

public sealed class DaprHealthCheckOptions
{
    public const string SectionName = "DaprHealth";

    public bool EnableStateStoreProbes { get; init; } = true;
    public bool EnablePubSubProbe { get; init; } = true;
    public string[] StateStores { get; init; } =
    [
        "manifest-state-store",
        "device-registry-state-store"
    ];
    public string PubSubComponent { get; init; } = "backup-events-pubsub";
    public string PubSubTopic { get; init; } = "health-probe";
    public int TimeoutSeconds { get; init; } = 5;
}
