using FlorisDeV.BackupApi.Models.State;

namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Exception thrown when a backup run operation cannot be performed due to invalid state.
/// For example, trying to commit a run that is already in Failed state.
/// </summary>
public class InvalidBackupRunStateException : Exception
{
    public Guid DeviceId { get; }
    public Guid RunId { get; }
    public BackupRunStatus CurrentStatus { get; }
    public BackupRunStatus ExpectedStatus { get; }

    public InvalidBackupRunStateException(
        Guid deviceId, 
        Guid runId, 
        BackupRunStatus currentStatus, 
        BackupRunStatus expectedStatus)
        : base($"Backup run '{runId}' for device '{deviceId}' is in '{currentStatus}' state. " +
               $"Expected state: '{expectedStatus}'")
    {
        DeviceId = deviceId;
        RunId = runId;
        CurrentStatus = currentStatus;
        ExpectedStatus = expectedStatus;
    }

    public InvalidBackupRunStateException(Guid deviceId, Guid runId, string message)
        : base(message)
    {
        DeviceId = deviceId;
        RunId = runId;
    }

    public InvalidBackupRunStateException(Guid deviceId, Guid runId, string message, Exception innerException)
        : base(message, innerException)
    {
        DeviceId = deviceId;
        RunId = runId;
    }
}
