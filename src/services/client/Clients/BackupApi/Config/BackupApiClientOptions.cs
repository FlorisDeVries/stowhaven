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
}