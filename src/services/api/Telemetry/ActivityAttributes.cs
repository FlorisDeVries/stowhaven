namespace FlorisDeV.BackupApi.Telemetry;

/// <summary>
///   Activity attribute names following OpenTelemetry semantic conventions.
/// </summary>
/// <remarks>
///   See: https://opentelemetry.io/docs/specs/semconv/
/// </remarks>
public static class ActivityAttributes
{
    // Device and backup run attributes
    public const string DeviceId = "backup.device_id";
    public const string RunId = "backup.run_id";
    public const string BackupRunStatus = "backup.run_status";
    
    // SAS URL attributes
    public const string SasUrlPath = "backup.sas_url.path";
    public const string SasUrlTtlMinutes = "backup.sas_url.ttl_minutes";
    public const string StorageAccount = "backup.storage.account";
    
    // State store attributes
    public const string StateKey = "backup.state.key";
    public const string StateETag = "backup.state.etag";
    
    // Secret store attributes
    public const string SecretName = "backup.secret.name";
    public const string SecretStoreComponent = "backup.secret_store.component";
    
    // Generic operation attributes
    public const string OperationName = "operation.name";
    public const string OperationStatus = "operation.status";
    
    // Error attributes
    public const string ErrorType = "error.type";
    public const string ErrorMessage = "error.message";
}
