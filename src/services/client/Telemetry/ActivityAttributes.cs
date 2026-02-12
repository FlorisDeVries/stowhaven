namespace FlorisDeV.BackupClient.Telemetry;

/// <summary>
///   Activity attribute names following OpenTelemetry semantic conventions.
/// </summary>
/// <remarks>
///   See: https://opentelemetry.io/docs/specs/semconv/
/// </remarks>
public static class ActivityAttributes
{
    // Custom backup-specific attributes
    public const string BackupSuccess = "backup.success";
    public const string BackupType = "backup.type";
    
    // Generic operation attributes
    public const string OperationName = "operation.name";
    public const string OperationStatus = "operation.status";
    
    // Error attributes
    public const string ErrorType = "error.type";
    public const string ErrorMessage = "error.message";
}
