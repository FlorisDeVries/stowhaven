namespace FlorisDeV.BackupContracts.Api.Responses;

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? TraceId { get; set; }
}