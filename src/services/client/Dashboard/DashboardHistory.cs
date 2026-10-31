using System.Text.Json;

namespace FlorisDeV.BackupClient.Dashboard;

public sealed record DashboardHistoryResult(string State, DashboardSnapshot[] Attempts, string? Message = null);

// Rolling observations are independent of recovery state and clean-evidence.json.
internal static class DashboardHistory
{
    internal const int Limit = 100;
    private sealed record HistoryFile(int SchemaVersion, DashboardSnapshot[] Attempts);

    internal static async Task RecordAsync(DashboardOptions options, DashboardSnapshot snapshot)
    {
        var previous = await ReadAsync(options);
        if (previous.State == "incompatible") return;
        var attempts = new[] { snapshot }.Concat(previous.Attempts.Where(a => a.AttemptId != snapshot.AttemptId)).Take(Limit).ToArray();
        var retained = attempts.Select(a => a.AttemptId).ToHashSet();
        attempts = attempts.Select(a => a with { Runs = a.Runs.Select(r => r with {
            AttemptIds = r.AttemptIds.Where(retained.Contains).Distinct().Take(Limit).ToArray() }).ToArray() }).ToArray();
        await PrivateFiles.ReplaceAsync(Path.Combine(options.ReportingDirectory, "history.json"),
            JsonSerializer.Serialize(new HistoryFile(DashboardReporting.SchemaVersion, attempts), DashboardReporting.Json));
    }

    internal static async Task<DashboardHistoryResult> ReadAsync(DashboardOptions options)
    {
        try
        {
            PrivateFiles.EnsureDirectory(options.ReportingDirectory);
            var path = Path.Combine(options.ReportingDirectory, "history.json");
            if (!File.Exists(path)) return new("available", []);
            var text = await PrivateFiles.ReadAsync(path);
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != DashboardReporting.SchemaVersion)
                return new("incompatible", [], "History schema is unsupported. Preserve the reporting files and upgrade the client and companion together.");
            var history = JsonSerializer.Deserialize<HistoryFile>(text, DashboardReporting.Json);
            if (history?.Attempts == null || history.Attempts.Length > Limit) throw new JsonException();
            var attempts = history.Attempts.Select(a => DashboardReporting.Decode(JsonSerializer.Serialize(a, DashboardReporting.Json))).ToArray();
            if (attempts.Any(a => a.State != "available" || a.Snapshot!.ClientActive || a.Snapshot.Result == "active")) throw new JsonException();
            return new("available", attempts.Select(a => a.Snapshot!).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        { return new("unavailable", [], "History cannot be read. Check private reporting permissions and run the client again."); }
    }
}
