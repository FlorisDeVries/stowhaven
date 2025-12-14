namespace FlorisDeV.BackupApi.Exceptions;

public class BackupRunNotFoundException(
    Guid deviceId,
    Guid runId
) : Exception($"Backup run '{runId}' not found for device '{deviceId}'")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid RunId { get; } = runId;
}