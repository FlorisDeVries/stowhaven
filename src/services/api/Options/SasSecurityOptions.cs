namespace FlorisDeV.BackupApi.Options;

public sealed class SasSecurityOptions
{
    public const string SectionName = "Backup:Sas";

    /// <summary>
    /// When enabled, upload SAS URLs are restricted to the request remote IP address.
    /// Disabled by default because SaaS clients may sit behind changing residential,
    /// carrier-grade NAT, VPN, or Container Apps proxy addresses.
    /// </summary>
    public bool EnableIpRestriction { get; set; }
}
