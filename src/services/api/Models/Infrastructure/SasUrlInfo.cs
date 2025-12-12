namespace FlorisDeV.BackupApi.Models.Infrastructure;

public class SasUrlInfo
{
    public required Uri Url { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int TtlMinutes { get; set; }
}