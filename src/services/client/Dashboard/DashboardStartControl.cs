using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Services;

namespace FlorisDeV.BackupClient.Dashboard;

public sealed record DashboardStartResult(string State, string Message, Guid? AttemptId = null, DateTimeOffset? StartedAt = null);

/// <summary>Requests a systemd start and waits for a client-owned execution acknowledgment.</summary>
public sealed class DashboardStartControl(DatabaseOptions database, BackupClientOptions backup, ISystemdUserService? systemd = null, TimeSpan? confirmationTimeout = null)
{
    private readonly ISystemdUserService _systemd = systemd ?? new SystemdUserService();
    private readonly TimeSpan _timeout = confirmationTimeout ?? TimeSpan.FromSeconds(10);
    internal string DatabasePath => database.GetDatabasePath();

    public async Task<DashboardStartResult> StartAsync(CancellationToken token = default)
    {
        if (!OperatingSystem.IsLinux()) return new("unsupported", "Dashboard starts require Linux systemd user services.");
        if (backup.Schedule.Enabled) return new("incompatibleSchedule", "Disable the resident client scheduler and use the existing systemd user service and timer.");
        try
        {
            using var reservation = BackupExecutionGuard.TryAcquire(database.GetDatabasePath(), launchReservation: true);
            if (reservation == null) return AlreadyRunning();
            var identity = reservation.Identity;
            using (var probe = BackupExecutionGuard.TryAcquire(database.GetDatabasePath()))
            {
                if (probe == null) return AlreadyRunning(await BackupExecutionReceipt.ReadAsync(backup.Dashboard, identity));
            }
            var before = await BackupExecutionReceipt.ReadAsync(backup.Dashboard, identity);
            var unit = backup.Dashboard.BackupServiceUnit;
            var status = await _systemd.ShowAsync(unit, token);
            if (status.LoadState != "loaded") return new("missingUnit", "The backup user service is missing. Install it for this user and reload the user systemd manager.");
            if (status.Type != "oneshot") return new("incompatibleSchedule", "Configure the backup service as Type=oneshot; resident backup services cannot be started safely from this dashboard.");
            if (status.ActiveState is "active" or "activating" or "deactivating" || status.HasJob)
                return AlreadyRunning(before?.InvocationId == status.InvocationId ? before : null);
            await _systemd.StartAsync(unit, token);
            var deadline = DateTimeOffset.UtcNow + _timeout;
            do
            {
                status = await _systemd.ShowAsync(unit, token);
                var receipt = await BackupExecutionReceipt.ReadAsync(backup.Dashboard, identity);
                if (receipt != null && receipt.AttemptId != before?.AttemptId && !string.IsNullOrEmpty(status.InvocationId) &&
                    string.Equals(receipt.InvocationId, status.InvocationId, StringComparison.OrdinalIgnoreCase))
                    return new("started", receipt.CompletedAt == null ? "Backup execution confirmed." : "Backup execution confirmed; the attempt has already finished.", receipt.AttemptId, receipt.StartedAt);
                if (status.ActiveState == "failed") return new("activationFailed", "The backup service failed before execution could be confirmed. Inspect its user journal and configuration, then retry.");
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            } while (DateTimeOffset.UtcNow < deadline);
            return new("unconfirmed", "The service request was accepted, but backup execution could not be confirmed. Check the user service journal, matching database/reporting configuration and reporting permissions before retrying. No additional attempt was queued.");
        }
        catch (SystemdControlException ex) { return new(ex.Category, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        { return new("protectionUnavailable", "Cannot safely protect backup execution. Check state database permissions and use a supported local filesystem. No start was requested."); }
    }
    private static DashboardStartResult AlreadyRunning(BackupExecutionReceipt? receipt = null)
        => new("alreadyRunning", "A backup attempt or start request is already running. No additional attempt was queued." +
            (receipt is { CompletedAt: null } ? " The attempt context is the latest recorded execution acknowledgment." : ""),
            receipt is { CompletedAt: null } ? receipt.AttemptId : null, receipt is { CompletedAt: null } ? receipt.StartedAt : null);
}
