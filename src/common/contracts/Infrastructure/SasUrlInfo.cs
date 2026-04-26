namespace FlorisDeV.BackupContracts.Infrastructure;

public class SasUrlInfo
{
    public required Uri Url { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int TtlMinutes { get; set; }
    public bool IsPathEmbedded { get; set; }
    public string? BasePath { get; set; }
}