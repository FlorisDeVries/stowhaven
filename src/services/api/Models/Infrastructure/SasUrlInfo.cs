namespace FlorisDeV.BackupApi.Models.Infrastructure;

public class SasUrlInfo
{
    public required Uri Url { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int TtlMinutes { get; set; }
    
    /// <summary>
    /// The base path/prefix where files should be uploaded within the container.
    /// Example: "staging/device-id/run-id/"
    /// </summary>
    public string? BasePath { get; set; }
}