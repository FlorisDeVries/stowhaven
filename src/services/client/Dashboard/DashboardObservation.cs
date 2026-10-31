using FlorisDeV.BackupClient.Services;

namespace FlorisDeV.BackupClient.Dashboard;

// Per-companion cache contains only a previously validated, sanitized reporting contract.
// Ownership is always probed independently; cached telemetry never authorizes a start.
internal sealed class DashboardObservation(DashboardOptions options, TimeProvider time, DashboardStartControl? control)
{
    private DashboardSnapshot? _lastVerified;

    internal async Task<DashboardStatus> ReadAsync()
    {
        var status = await DashboardReporting.ReadAsync(options, time);
        if (status.Snapshot is { } verified) _lastVerified = verified;
        else if (_lastVerified != null)
            status = status with { Snapshot = _lastVerified,
                State = status.State == "empty" ? "unavailable" : status.State,
                Message = status.State == "empty" ? "Reporting is no longer available. Showing the last verified observation; current activity and completion are unknown." : status.Message };
        var execution = "unavailable";
        string? identity = null;
        int? ownerProcessId = null;
        if (control != null)
        {
            try
            {
                var probe = BackupExecutionGuard.Observe(control.DatabasePath);
                identity = probe.Identity;
                ownerProcessId = probe.OwnerProcessId;
                execution = probe.Active ? "active" : "inactive";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { }
        }
        status = status with { ExecutionState = execution };
        if (status.State != "available") return status with { Observation = "unavailable" };
        var snapshot = status.Snapshot!;
        var receipt = identity == null ? null : await BackupExecutionReceipt.ReadAsync(options, identity);
        if (receipt != null && receipt.AttemptId != snapshot.AttemptId)
            return status with { Observation = "unavailable", Message = "The latest execution has no matching observation. Showing the last verified snapshot; its result does not describe current execution." };
        if (snapshot.Result != "active") return status with { Observation = "recorded" };
        if (receipt?.AttemptId == snapshot.AttemptId && (execution == "inactive" || ExecutionProcessIdentity.HasEnded(receipt)))
            return status with { Observation = "interrupted", Message = "Backup execution ended without a recorded final result. Last verified evidence is preserved; completion is unknown." };
        if (control != null && (execution == "unavailable" || receipt?.ProcessIdentity == null || receipt.ProcessId is not > 0 || ownerProcessId != receipt.ProcessId))
            return status with { Observation = "unavailable", Message = "The recorded attempt's execution identity cannot be verified. Current execution ownership is reported separately; last verified evidence is preserved." };
        return status with { Observation = status.Stale ? "stale" : "fresh",
            Message = status.Stale ? "No heartbeat for more than 15 seconds. Silence does not establish that backup execution ended." : null };
    }
}
