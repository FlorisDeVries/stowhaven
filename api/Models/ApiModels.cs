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
    public string SasUrl { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public int TtlMinutes { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
