using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FlorisDeV.BackupClient.Dashboard;

public sealed record SystemdServiceState(string LoadState, string ActiveState, string Type, string? InvocationId = null, bool HasJob = false, bool RemainAfterExit = false)
{
    internal bool SupportsBackupAttempts => Type == "oneshot" && !RemainAfterExit;
}
public sealed record SystemdTimerState(string LoadState, string ActiveState, string UnitFileState, DateTimeOffset? NextActivation, string? ServiceUnit = null);
public interface ISystemdUserService
{
    Task<SystemdServiceState> ShowAsync(string unit, CancellationToken token);
    Task<SystemdTimerState> ShowTimerAsync(string unit, CancellationToken token)
        => throw new SystemdControlException("schedulerUnavailable", "Timer information is unavailable. Upgrade the companion and check systemd user-manager access.");
    Task StartAsync(string unit, CancellationToken token);
}
public sealed class SystemdControlException(string category, string message) : Exception(message)
{
    public string Category { get; } = category;
}

/// <summary>Linux user-manager boundary. No shell, direct execution, or secret-bearing journal output.</summary>
public sealed class SystemdUserService : ISystemdUserService
{
    public async Task<SystemdServiceState> ShowAsync(string unit, CancellationToken token)
    {
        var output = await RunAsync(unit, ["show", "--property=LoadState,ActiveState,Type,InvocationID,Job,RemainAfterExit"], token);
        var values = output.Split('\n').Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1]);
        return new(values.GetValueOrDefault("LoadState", "unknown"), values.GetValueOrDefault("ActiveState", "unknown"),
            values.GetValueOrDefault("Type", "unknown"), values.GetValueOrDefault("InvocationID"), values.TryGetValue("Job", out var job) && job.Length > 0 && job != "0",
            values.GetValueOrDefault("RemainAfterExit") == "yes");
    }
    public async Task StartAsync(string unit, CancellationToken token) => _ = await RunAsync(unit, ["start", "--no-block", "--job-mode=fail"], token);

    public async Task<SystemdTimerState> ShowTimerAsync(string unit, CancellationToken token)
    {
        var output = await RunAsync(unit, ["show", "--all", "--timestamp=unix", "--property=LoadState,ActiveState,UnitFileState,NextElapseUSecRealtime,Unit"], token, timer: true);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var pair = line.Split('=', 2);
            if (pair.Length == 2 && !values.TryAdd(pair[0], pair[1]))
                throw new SystemdControlException("schedulerUnavailable", "The user manager returned ambiguous timer information. Check the timer configuration and retry.");
        }
        DateTimeOffset? next = null;
        var timestamp = values.GetValueOrDefault("NextElapseUSecRealtime");
        // systemctl --timestamp=unix prints @seconds-since-epoch. Never parse localized dates
        // or calculate a next activation from the configured calendar.
        if (timestamp?.StartsWith('@') == true && decimal.TryParse(timestamp.AsSpan(1), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var seconds) && seconds > 0 && seconds <= 253402300799)
            next = DateTimeOffset.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond));
        return new(values.GetValueOrDefault("LoadState", "unknown"), values.GetValueOrDefault("ActiveState", "unknown"),
            values.GetValueOrDefault("UnitFileState", "unknown"), next, values.GetValueOrDefault("Unit"));
    }

    private static async Task<string> RunAsync(string unit, string[] arguments, CancellationToken token, bool timer = false)
    {
        if (unit == null || !Regex.IsMatch(unit, "\\A[a-zA-Z0-9_.@:-]{1,200}\\." + (timer ? "timer" : "service") + "\\z", RegexOptions.None, TimeSpan.FromSeconds(1)))
            throw new SystemdControlException("invalidUnit", "Configure an exact backup systemd " + (timer ? "timer" : "service") + " unit name, without paths or patterns.");
        var start = new ProcessStartInfo("systemctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--user");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(unit);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new SystemdControlException("userSessionUnavailable", "Cannot contact the systemd user manager. Sign in with an active user session and retry.");
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout; var error = await stderr;
            if (output.Length > 16384 || error.Length > 16384)
                throw new SystemdControlException("schedulerUnavailable", "The user manager returned too much information. Check the configured backup units and retry.");
            if (process.ExitCode == 0 || (arguments[0] == "show" && output.Contains("LoadState=not-found", StringComparison.Ordinal))) return output;
            if (error.Contains("bus", StringComparison.OrdinalIgnoreCase)) throw new SystemdControlException("userSessionUnavailable", "Cannot contact the systemd user manager. Sign in with an active user session and check XDG_RUNTIME_DIR and the user bus.");
            if (error.Contains("denied", StringComparison.OrdinalIgnoreCase) || error.Contains("authentication", StringComparison.OrdinalIgnoreCase)) throw new SystemdControlException("permissionDenied", "The user manager denied the request. Check user-service permissions and authorization.");
            if (error.Contains("not found", StringComparison.OrdinalIgnoreCase) || error.Contains("not loaded", StringComparison.OrdinalIgnoreCase)) throw new SystemdControlException("missingUnit", "The backup user service is missing. Install it for this user and reload the user systemd manager.");
            if (arguments[0] == "show") throw new SystemdControlException("schedulerUnavailable", "The user manager could not provide scheduling information. Check the units and use systemd 251 or newer for machine-readable timer timestamps.");
            throw new SystemdControlException("activationFailed", "The user manager rejected the backup start. Check the backup service configuration and user journal.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new SystemdControlException("userSessionUnavailable", "The systemd user manager did not respond. Check the user session and user bus before retrying."); }
        catch (System.ComponentModel.Win32Exception)
        { throw new SystemdControlException("userSessionUnavailable", "systemctl is unavailable. Install systemd and run the companion in the backup user's active session."); }
        finally { if (process != null) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); } }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024]; var text = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0) { if (text.Length < 16385) text.Append(buffer, 0, Math.Min(count, 16385 - text.Length)); }
        return text.ToString();
    }
}
