using FlorisDeV.BackupApi.Models.State;

namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Exception thrown when a backup run operation cannot be performed due to invalid state.
/// For example, trying to commit a run that is already in Failed state.
/// </summary>
public class InvalidBackupRunStateException(
    Guid deviceId,
    Guid runId,
    BackupRunStatus currentStatus,
    BackupRunStatus expectedStatus)
    : Exception($"Backup run '{runId}' for device '{deviceId}' is in '{currentStatus}' state. " +
                $"Expected state: '{expectedStatus}'")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid RunId { get; } = runId;
    public BackupRunStatus CurrentStatus { get; } = currentStatus;
    public BackupRunStatus ExpectedStatus { get; } = expectedStatus;
}
