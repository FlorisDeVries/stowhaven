using System.Text.Json;

namespace FlorisDeV.BackupClient.Dashboard;

// Execution acknowledgments are independent of optional live snapshots and contain no state payloads.
internal sealed record BackupExecutionReceipt(Guid AttemptId, string StateIdentity, string? InvocationId,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null)
{
    public int? ProcessId { get; init; }
    public string? ProcessIdentity { get; init; }
    internal static async Task WriteAsync(DashboardOptions options, BackupExecutionReceipt receipt)
    {
        if (!options.Enabled) return;
        PrivateFiles.EnsureDirectory(options.ReportingDirectory);
        await PrivateFiles.ReplaceAsync(Path.Combine(options.ReportingDirectory, "execution.json"), JsonSerializer.Serialize(receipt, DashboardReporting.Json));
    }
    internal static async Task<BackupExecutionReceipt?> ReadAsync(DashboardOptions options, string identity)
    {
        try
        {
            var value = JsonSerializer.Deserialize<BackupExecutionReceipt>(await PrivateFiles.ReadAsync(Path.Combine(options.ReportingDirectory, "execution.json")), DashboardReporting.Json);
            return value is { AttemptId: var id, StartedAt: var start } && id != Guid.Empty && start != default && value.StateIdentity == identity &&
                (value.CompletedAt == null || value.CompletedAt >= start) &&
                (value.ProcessIdentity == null || (value.ProcessId > 0 && System.Text.RegularExpressions.Regex.IsMatch(value.ProcessIdentity, "\\A[a-fA-F0-9-]{36}:[0-9]{1,20}\\z"))) &&
                (value.InvocationId == null || System.Text.RegularExpressions.Regex.IsMatch(value.InvocationId, "\\A[a-fA-F0-9]{32}\\z")) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
}

// Linux kernel start ticks plus boot identity distinguish a process from a reused PID.
// A matching live process is not proof that it still owns execution; the inode probe
// remains the independent authority for duplicate protection.
internal static class ExecutionProcessIdentity
{
    internal static string? Read(int processId)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        if (!Guid.TryParseExact(boot, "D", out _) || fields.Length < 20 || !ulong.TryParse(fields[19], out _)) return null;
        return boot + ":" + fields[19];
    }
    internal static bool HasEnded(BackupExecutionReceipt receipt)
    {
        if (receipt.ProcessId is not > 0 || receipt.ProcessIdentity == null) return false;
        try { return Read(receipt.ProcessId.Value) is { } current && current != receipt.ProcessIdentity; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
