using System.ComponentModel.DataAnnotations;

namespace BackupApi.Models;

public class SasRequest
{
    [Required(ErrorMessage = "Path is required")]
    [StringLength(1024, MinimumLength = 1, ErrorMessage = "Path must be between 1 and 1024 characters")]
    public string? Path { get; set; }
    
    [Range(1, 240, ErrorMessage = "TTL must be between 1 and 240 minutes")]
    public int? TtlMinutes { get; set; }
}

public class SasResponse
{
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public int TtlMinutes { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public class HealthStatus
{
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
}

public class BackupCompletedEvent
{
    public string Path { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long? FileSizeBytes { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? BackupId { get; set; }
    public string ClientId { get; set; } = string.Empty;
}
