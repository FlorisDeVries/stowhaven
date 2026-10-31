namespace FlorisDeV.BackupClient.Dashboard;

public sealed record DashboardDiagnostic(DateTimeOffset At, string Category, string TargetName, string RelativePath, string Action);

public sealed record DashboardDiagnostics
{
    public int Skipped { get; init; }
    public int Changed { get; init; }
    public int Deferred { get; init; }
    public int Failed { get; init; }
    public int ServerRejected { get; init; }
    public bool Truncated { get; init; }
    public DashboardDiagnostic[] Entries { get; init; } = [];

    internal static bool Valid(DashboardDiagnostics? value, DashboardSnapshot snapshot)
        => value != null && value.Skipped >= 0 && value.Changed >= 0 && value.Deferred >= 0 && value.Failed >= 0 && value.ServerRejected >= 0 &&
           value.Entries != null && value.Entries.Length <= 100 &&
           (value.Truncated || (long)value.Skipped + value.Changed + value.Deferred + value.Failed + value.ServerRejected <= value.Entries.Length) &&
           value.Entries.All(e => e != null && e.At >= snapshot.StartedAt && e.At <= snapshot.ObservedAt &&
               DashboardDiagnosticCollector.Actions.TryGetValue(e.Category, out var action) && e.Action == action &&
               e.TargetName == DashboardReporting.SafeTarget(e.TargetName) && DashboardDiagnosticCollector.SafePath(e.RelativePath) == e.RelativePath);
}

internal sealed class DashboardDiagnosticCollector(TimeProvider time)
{
    internal static bool SensitiveName(string value) => System.Text.RegularExpressions.Regex.IsMatch(value,
        @"(?i)bearer|authorization|token|secret|password|credential|recovery|mnemonic|seed|\bsas\b|connectionstring|cookie|\bsession\b|sharedaccesssignature|[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}|[a-z0-9_-]{40,}");
    internal static readonly IReadOnlyDictionary<string, string> Actions = new Dictionary<string, string>
    {
        ["skipped"] = "Check file access and readability, then retry the backup.",
        ["changed"] = "The file changed while backing up. Retry after it stops changing.",
        ["deferred"] = "Upload authorization expired. Retry the backup to obtain fresh authorization.",
        ["failed"] = "Upload failed. Check connectivity and file access, then retry.",
        ["serverRejected"] = "The server rejected this file. Retry the backup; investigate repeated failures."
    };
    private readonly Dictionary<string, int> _counts = new();
    private readonly List<DashboardDiagnostic> _entries = [];
    internal static string SafePath(string value)
    {
        if (value == "[redacted path]") return value;
        // Reject URLs, query fragments, token-like names, control characters and path traversal
        // before persistence. Never accept exception messages or server error text.
        if (value.Length is < 1 or > 256 || value.StartsWith('/') || value.Contains('\\') ||
            value.Split('/').Any(p => p is "" or "." or "..") || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || " /_.-()".Contains(c))) ||
            SensitiveName(value))
            return "[redacted path]";
        return value;
    }
    internal void Add(string category, string target, string path)
    {
        _counts[category] = _counts.GetValueOrDefault(category) + 1;
        if (_entries.Count < 100) _entries.Add(new(time.GetUtcNow(), category, DashboardReporting.SafeTarget(target), SafePath(path), Actions[category]));
    }
    internal void Missing(string category, int count) => _counts[category] = _counts.GetValueOrDefault(category) + count;
    internal DashboardDiagnostics Capture() => new()
    {
        Skipped = _counts.GetValueOrDefault("skipped"), Changed = _counts.GetValueOrDefault("changed"), Deferred = _counts.GetValueOrDefault("deferred"),
        Failed = _counts.GetValueOrDefault("failed"), ServerRejected = _counts.GetValueOrDefault("serverRejected"),
        Truncated = _counts.Values.Sum() > _entries.Count, Entries = _entries.ToArray()
    };
}
