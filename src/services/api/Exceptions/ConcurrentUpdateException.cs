namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Exception thrown when a concurrent update conflict is detected (ETag mismatch).
/// This indicates that the resource was modified by another request between the read and write operations.
/// </summary>
public class ConcurrentUpdateException : Exception
{
    public Guid DeviceId { get; }
    public Guid RunId { get; }
    public string? ExpectedETag { get; }
    public string? ActualETag { get; }

    public ConcurrentUpdateException(Guid deviceId, Guid runId, string? expectedETag, string? actualETag)
        : base($"Concurrent update detected for backup run '{runId}' of device '{deviceId}'. " +
               $"The resource has been modified by another request. Please retry the operation.")
    {
        DeviceId = deviceId;
        RunId = runId;
        ExpectedETag = expectedETag;
        ActualETag = actualETag;
    }

    public ConcurrentUpdateException(Guid deviceId, Guid runId, string message)
        : base(message)
    {
        DeviceId = deviceId;
        RunId = runId;
    }

    public ConcurrentUpdateException(Guid deviceId, Guid runId, string message, Exception innerException)
        : base(message, innerException)
    {
        DeviceId = deviceId;
        RunId = runId;
    }
}
