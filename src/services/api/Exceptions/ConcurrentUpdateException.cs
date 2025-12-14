namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Exception thrown when a concurrent update conflict is detected (ETag mismatch).
/// This indicates that the resource was modified by another request between the read and write operations.
/// </summary>
public class ConcurrentUpdateException(
    Guid deviceId,
    Guid runId,
    string? expectedETag,
    string? actualETag
) : Exception($"Concurrent update detected for backup run '{runId}' of device '{deviceId}'. " +
              $"The resource has been modified by another request. Please retry the operation.")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid RunId { get; } = runId;
    public string? ExpectedETag { get; } = expectedETag;
    public string? ActualETag { get; } = actualETag;
}