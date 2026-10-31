using FlorisDeV.BackupClient.Config;

namespace FlorisDeV.BackupClient.Dashboard;

public sealed record DashboardSchedulingStatus(string State, string Message, bool? Enabled = null,
    DateTimeOffset? NextActivation = null, string NextActivationState = "unknown", string ActiveState = "unknown");

// Scheduler state is observed on demand and never persisted as backup freshness evidence.
internal sealed class DashboardScheduling(DashboardOptions options, DashboardStartControl? control,
    Func<BackupClientOptions>? currentOptions = null)
{
    internal async Task<DashboardSchedulingStatus> ReadAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return new("unsupported", "Scheduling information requires Linux systemd user services.");
        try
        {
            var backup = currentOptions?.Invoke() ?? control?.BackupOptions;
            if (backup == null) return new("unavailable", "Schedule configuration is unavailable. Restart the companion with the backup service configuration.");
            if (backup.Schedule.Enabled) return new("incompatibleSchedule", "Disable BackupClient:Schedule:Enabled (the resident scheduler) and use the systemd Type=oneshot user service and timer. The companion does not change this configuration.");
            var dashboard = currentOptions == null ? options : backup.Dashboard;
            var systemd = control?.Systemd ?? new SystemdUserService();
            var service = await systemd.ShowAsync(dashboard.BackupServiceUnit, token);
            if (service.LoadState == "not-found") return new("missingUnit", "The backup user service is missing. Install it for this user and reload the user systemd manager.");
            if (service.LoadState != "loaded") return new("unavailable", "The backup service cannot be loaded. Check its systemd user unit configuration and reload the user manager.");
            if (service.Type != "oneshot" || service.RemainAfterExit) return new("incompatibleSchedule", "Configure the backup user service as Type=oneshot with RemainAfterExit=no and disable the resident scheduler. The service must exit between attempts so its timer can activate it again.");
            var timer = await systemd.ShowTimerAsync(dashboard.BackupTimerUnit, token);
            if (timer.LoadState == "not-found") return new("missingUnit", "The backup user timer is missing. Install it for this user and reload the user systemd manager.");
            if (timer.LoadState == "masked") return new("masked", "The backup user timer is masked. Review its configuration and unmask it if scheduled attempts are intended.", false);
            if (timer.LoadState != "loaded") return new("unavailable", "The backup timer cannot be loaded. Check its systemd user unit configuration and reload the user manager.");
            if (timer.ServiceUnit != dashboard.BackupServiceUnit) return new("incompatibleSchedule", "The timer must activate the configured backup user service. Correct its Unit= setting or the dashboard unit configuration and reload the user manager.");
            bool? enabled = timer.UnitFileState switch { "enabled" or "enabled-runtime" => true, "disabled" or "masked" or "masked-runtime" => false, _ => null };
            var state = timer.UnitFileState is "masked" or "masked-runtime" ? "masked" : enabled == true ? "enabled" : enabled == false ? "disabled" : "unknown";
            var next = timer.NextActivation;
            var message = state switch
            {
                "disabled" => "The timer is disabled for automatic enablement. Enable the backup user timer if scheduled attempts are intended; a currently active timer can still fire.",
                "masked" => "The timer is masked. Review the backup user timer configuration and unmask it if scheduled attempts are intended.",
                "unknown" => "The timer's enabled state is unknown. Check its systemd user unit installation; transient or static units do not establish persistent enablement.",
                _ => timer.ActiveState == "active" ? "The backup user timer is enabled. Activation is scheduling information only; it does not establish a clean backup outcome." : "The timer is enabled but is not active. Inspect and start the backup user timer if scheduled attempts are intended."
            };
            if (next == null) message += " The user manager did not provide a known next activation; check the timer state. No next time is inferred from configuration.";
            var active = timer.ActiveState is "active" or "inactive" or "failed" or "activating" or "deactivating" ? timer.ActiveState : "unknown";
            return new(state, message, enabled, next, next == null ? "unknown" : "known", active);
        }
        catch (SystemdControlException ex) { return new(ex.Category, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return new("unavailable", "Scheduling information is unavailable. Check the backup configuration and systemd user-manager access."); }
    }
}
