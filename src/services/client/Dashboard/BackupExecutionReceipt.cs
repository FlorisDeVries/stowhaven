using System.Text.Json;

namespace FlorisDeV.BackupClient.Dashboard;

// Execution acknowledgments are independent of optional live snapshots and contain no state payloads.
internal sealed record BackupExecutionReceipt(Guid AttemptId, string StateIdentity, string? InvocationId,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null)
{
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
                (value.InvocationId == null || System.Text.RegularExpressions.Regex.IsMatch(value.InvocationId, "\\A[a-fA-F0-9]{32}\\z")) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
}
