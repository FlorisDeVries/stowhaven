namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Exception thrown when attempting to commit a backup run that has already been committed.
/// </summary>
public class BackupRunAlreadyCommittedException : Exception
{
    public Guid DeviceId { get; }
    public Guid RunId { get; }

    public BackupRunAlreadyCommittedException(Guid deviceId, Guid runId)
        : base($"Backup run '{runId}' for device '{deviceId}' has already been committed")
    {
        DeviceId = deviceId;
        RunId = runId;
    }

    public BackupRunAlreadyCommittedException(Guid deviceId, Guid runId, string message)
        : base(message)
    {
        DeviceId = deviceId;
        RunId = runId;
    }

    public BackupRunAlreadyCommittedException(Guid deviceId, Guid runId, string message, Exception innerException)
        : base(message, innerException)
    {
        DeviceId = deviceId;
        RunId = runId;
    }
}
