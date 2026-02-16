namespace FlorisDeV.BackupClient.Authentication;

public class AzureAdOptions
{
    public const string SectionName = "AzureAd";

    public required string Instance { get; set; }
    public required string TenantId { get; set; }
    public required string ClientId { get; set; }
}
