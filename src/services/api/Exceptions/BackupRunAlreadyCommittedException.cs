namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Exception thrown when attempting to commit a backup run that has already been committed.
/// </summary>
public class BackupRunAlreadyCommittedException(
    Guid deviceId,
    Guid runId
) : Exception($"Backup run '{runId}' for device '{deviceId}' has already been committed")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid RunId { get; } = runId;
}