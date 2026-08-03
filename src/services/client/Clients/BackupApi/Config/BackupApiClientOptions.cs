namespace FlorisDeV.BackupClient.Clients.BackupApi.Config;

public class BackupApiClientOptions
{
    public const string DefaultSectionName = "BackupApiClient";
    public const string DefaultRetrySectionName = "BackupApiClient:RetryOptions";

    /// <summary>
    ///   The api url for connecting to routty connector api used
    ///   for fetching upload urls for posting new documents
    /// </summary>
    public required string ApiUrl { get; set; }

    public required string AuthenticationScope { get; set; }

    public required string AuthenticationTenant { get; set; }

    /// <summary>
    /// Controls the pre-flight wake-up ping used to bring a scaled-to-zero API/gateway
    /// online before the client sends real requests.
    /// </summary>
    public ApiWakeUpOptions WakeUp { get; init; } = new();
}

public sealed class ApiWakeUpOptions
{
    /// <summary>
    /// Whether to ping the API before starting real work. Default is true.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Delay before the first retry, in seconds. Doubles after each failed attempt. Default is 2s.
    /// </summary>
    public int InitialDelaySeconds { get; init; } = 2;

    /// <summary>
    /// Maximum delay between retry attempts, in seconds. Default is 30s.
    /// </summary>
    public int MaxDelaySeconds { get; init; } = 30;

    /// <summary>
    /// Maximum total time to wait for the API to wake up before giving up, in seconds. Default is 180s (3 minutes).
    /// </summary>
    public int MaxWaitSeconds { get; init; } = 180;

    /// <summary>
    /// Maximum duration of one anonymous health probe. Default is 10 seconds.
    /// </summary>
    public int ProbeTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// How long a successful probe is reused by subsequent API calls. Default is 60 seconds.
    /// Set to 0 to probe before every API request.
    /// </summary>
    public int RecheckIntervalSeconds { get; init; } = 60;
}
