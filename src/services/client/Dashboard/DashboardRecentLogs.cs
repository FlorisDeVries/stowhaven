using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FlorisDeV.BackupClient.Dashboard;

internal sealed record DashboardLogView(string State, string Message, string[] Entries, bool Truncated = false);

/// <summary>Reads a fixed recent client source; raw input and derived views never go to disk.</summary>
internal static class DashboardRecentLogs
{
    private const int SourceBytes = 32 * 1024;
    private static readonly Regex SourceName = new(@"\Abackup-client-[0-9]{8}\.log\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EventStart = new(@"(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[(?:VRB|DBG|INF|WRN|ERR|FTL)\] ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    // Suppress the whole event, including multiline values/exceptions, rather than trying
    // to guess where an untrusted credential ends. URLs can carry credentials in any part.
    private static readonly Regex Sensitive = new(@"bearer|authorization|token|secret|password|\bpwd\b|credential|recovery|mnemonic|seed|\bsas\b|connectionstring|cookie|\bsession\b|key\b\s*[""']?\s*[=:]|sharedaccesssignature|(?:^|[\s?&""'])sig(?:\s*[""']?\s*[=:]|%3d)|\bbasic\s+[a-z0-9+/]+=*|[a-z][a-z0-9+.-]*(?:://|%3a%2f%2f)|-----BEGIN|[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static async Task<DashboardLogView> ReadAsync(DashboardOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(options.LogDirectory)) return new("missing", "No recent client logs. Run a backup attempt, then open recent logs again.", []);
            DashboardLogSources.ValidateDirectory(options.LogDirectory);
            var paths = Directory.EnumerateFiles(options.LogDirectory, "backup-client-*.log")
                .Where(IsClientSource).Take(65).OrderDescending(StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) return new("missing", "No recent client logs. Run a backup attempt, then open recent logs again.", []);
            if (paths.Length > 64) return new("unavailable", "Too many recent client log sources. Check the client's log directory and rotation configuration.", []);
            var entries = new List<string>();
            var truncated = paths.Length > 2;
            foreach (var path in paths.Take(2).Reverse())
            {
                await using var stream = DashboardLogSources.Open(path);
                var length = stream.Length;
                var start = Math.Max(0, length - SourceBytes);
                stream.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[(int)(length - start)];
                await stream.ReadExactlyAsync(buffer, cancellationToken);
                var text = Encoding.UTF8.GetString(buffer);
                // Even a timestamp-shaped prefix at a byte cutoff may belong to a
                // secret value. Never parse that partial first line as a new event.
                if (start > 0)
                {
                    var newline = text.IndexOf('\n');
                    text = newline < 0 ? "" : text[(newline + 1)..];
                }
                var starts = EventStart.Matches(text);
                truncated |= start > 0 || (text.Length > 0 && (starts.Count == 0 || starts[0].Index > 0));
                for (var i = 0; i < starts.Count; i++)
                {
                    var end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
                    var entry = text[starts[i].Index..end];
                    if (i == starts.Count - 1 && !entry.EndsWith('\n')) { truncated = true; continue; }
                    entries.Add(Sensitive.IsMatch(entry) ? "[redacted sensitive log entry]" : entry.TrimEnd());
                }
            }
            while (entries.Count > 200 || entries.Sum(e => e.Length) > 32768)
            { entries.RemoveAt(0); truncated = true; }
            if (entries.Count == 0) return new("empty", "No complete recent client log entries fit this view. Allow the current entry to finish, then retry.", [], truncated);
            return new("available", "Recent client logs. Sensitive entries are redacted. Refresh to see newer entries.", entries.ToArray(), truncated);
        }
        catch (FileNotFoundException) { return new("rotated", "Client logs rotated while opening. Open recent logs again.", []); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        { return new("unavailable", "Recent client logs cannot be read. Check the client's log directory and permissions, then retry.", []); }
    }

    private static bool IsClientSource(string path)
    {
        var name = Path.GetFileName(path);
        return SourceName.IsMatch(name) && DateOnly.TryParseExact(name.AsSpan(14, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
}
