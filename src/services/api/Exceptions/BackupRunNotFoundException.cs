namespace FlorisDeV.BackupApi.Exceptions;

public class BackupRunNotFoundException : Exception
{
    public Guid DeviceId { get; }
    public Guid RunId { get; }

    public BackupRunNotFoundException(Guid deviceId, Guid runId)
        : base($"Backup run '{runId}' not found for device '{deviceId}'")
    {
        DeviceId = deviceId;
        RunId = runId;
    }

    public BackupRunNotFoundException(Guid deviceId, Guid runId, string message)
        : base(message)
    {
        DeviceId = deviceId;
        RunId = runId;
    }

    public BackupRunNotFoundException(Guid deviceId, Guid runId, string message, Exception innerException)
        : base(message, innerException)
    {
        DeviceId = deviceId;
        RunId = runId;
    }
}
