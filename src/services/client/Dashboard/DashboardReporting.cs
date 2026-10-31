using System.Security.Cryptography;
using System.Runtime.InteropServices;
using FlorisDeV.BackupClient.Services;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlorisDeV.BackupClient.Models;

namespace FlorisDeV.BackupClient.Dashboard;

public sealed class DashboardOptions
{
    public bool Enabled { get; init; }
    public string ReportingDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "backup-client", "reporting");
    public int Port { get; init; } = 0;
    public string BackupServiceUnit { get; init; } = "backup-client.service";
}

public sealed record DashboardSnapshot(
    int SchemaVersion, Guid AttemptId, DateTimeOffset StartedAt, DateTimeOffset? ScanStartedAt,
    DateTimeOffset ObservedAt, string Result, string[] TargetNames, string SelectionFingerprint,
    int FilesScanned, string ServerBackupEvidence)
{
    public bool ClientActive { get; init; }
    public BackupRunResult[] Runs { get; init; } = [];
    public BackupScanEvidence? CurrentScan { get; init; }
    public DashboardLiveProgress? Live { get; init; }
}

public sealed record DashboardStatus(string State, DashboardSnapshot? Snapshot = null, string? Message = null, bool Stale = false);

// Only this allow-listed portable contract reaches disk or HTTP. Never serialize BackupResult/state/configuration.
internal static class DashboardReporting
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const int SchemaVersion = 1;
    internal static string SafeTarget(string value) => Regex.IsMatch(value, "\\A[a-zA-Z0-9_.-]{1,64}\\z") ? value : "[redacted target]";

    private static string[] SafeWarnings(DashboardLiveProgress progress)
        => new[] { progress.FilesSkipped > 0 ? "Some files were skipped. Review the selected files and retry." : null,
            progress.FilesDeferred > 0 ? "Some files were deferred and need another backup attempt." : null,
            progress.FilesFailed > 0 ? "Some files failed to upload and need another backup attempt." : null }.OfType<string>().ToArray();

    internal static string Fingerprint(IReadOnlyDictionary<string, string> targets, string[]? exclusions)
    {
        var selection = JsonSerializer.Serialize(new { Targets = targets.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { Name = x.Key, Path = x.Value, LocalExclusions = BackupIgnoreParser.GetCombinedPatterns(Path.Combine(x.Value, ".backupignore")) ?? [] }), Exclusions = exclusions ?? [] });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selection))).ToLowerInvariant();
    }

    internal static async Task WriteAsync(DashboardOptions options, DashboardSnapshot snapshot)
    {
        if (!ValidRunEvidence(snapshot)) throw new InvalidOperationException("Invalid run evidence cannot be reported.");
        PrivateFiles.EnsureDirectory(options.ReportingDirectory);
        var path = Path.Combine(options.ReportingDirectory, "snapshot.json");
        if (File.Exists(path))
        {
            var current = await ReadAsync(options);
            if (current.State == "incompatible") return; // Older producers preserve newer data, too.
        }
        await PrivateFiles.ReplaceAsync(path, JsonSerializer.Serialize(snapshot, Json));
    }

    internal static async Task<DashboardStatus> ReadAsync(DashboardOptions options, TimeProvider? timeProvider = null)
    {
        try
        {
            PrivateFiles.EnsureDirectory(options.ReportingDirectory);
            var path = Path.Combine(options.ReportingDirectory, "snapshot.json");
            if (!File.Exists(path)) return new("empty", Message: "No backup attempt has been recorded.");
            var text = await PrivateFiles.ReadAsync(path);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schema) || schema != SchemaVersion)
                return new("incompatible", Message: "Reporting schema is unsupported. Upgrade the client and dashboard together; preserve the reporting files.");
            var snapshot = JsonSerializer.Deserialize<DashboardSnapshot>(text, Json)!;
            if (snapshot.AttemptId == Guid.Empty || snapshot.TargetNames == null ||
                snapshot.StartedAt == default || snapshot.ObservedAt < snapshot.StartedAt ||
                (snapshot.ScanStartedAt is { } scan && (scan < snapshot.StartedAt || scan > snapshot.ObservedAt)) ||
                (snapshot.Result == "cleanCheck" && snapshot.ScanStartedAt == null) ||
                (snapshot.Result == "active" && snapshot.Live is not { Active: true }) ||
                !Regex.IsMatch(snapshot.SelectionFingerprint ?? "", "\\A[a-f0-9]{64}\\z") ||
                !ValidRunEvidence(snapshot) || snapshot.FilesScanned < 0 || snapshot.Result is not ("cleanCheck" or "noChanges" or "succeeded" or "completedWithErrors" or "pending" or "failed" or "active") ||
                snapshot.ServerBackupEvidence is not ("none" or "recorded" or "unknown"))
                return new("unavailable", Message: "Reporting is invalid. Run the client again to record a new observation.");
            if (snapshot.Live is { } live && (live.HeartbeatAt < snapshot.StartedAt || live.HeartbeatAt > snapshot.ObservedAt ||
                live.FilesUnchanged < 0 || live.FilesUploaded < 0 || live.FilesSkipped < 0 || live.FilesDeferred < 0 || live.FilesFailed < 0 ||
                live.UploadedBytes < 0 || live.ElapsedSeconds < 0 || !double.IsFinite(live.ElapsedSeconds) ||
                live.AverageBytesPerSecond < 0 || !double.IsFinite(live.AverageBytesPerSecond) ||
                live.AverageFilesPerSecond < 0 || !double.IsFinite(live.AverageFilesPerSecond) ||
                live.TotalFilesToScan < snapshot.FilesScanned || live.TotalBytesToUpload < live.UploadedBytes ||
                live.UploadPercent != null || live.EstimatedSecondsRemaining != null))
                return new("unavailable", Message: "Live reporting is invalid. Run the client again to record a new observation.");
            return new("available", snapshot with { TargetNames = snapshot.TargetNames.Select(SafeTarget).ToArray(),
                Live = snapshot.Live is { } progress ? progress with { CurrentTarget = progress.CurrentTarget == null ? null : SafeTarget(progress.CurrentTarget),
                    Warnings = SafeWarnings(progress) } : null },
                Stale: snapshot.Live is { Active: true } current && (timeProvider ?? TimeProvider.System).GetUtcNow() - current.HeartbeatAt > TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        { return new("unavailable", Message: "Reporting cannot be read. Check private reporting permissions and run the client again."); }
    }
    private static bool ValidRunEvidence(DashboardSnapshot snapshot)
    {
        static bool Fingerprint(string? value) => value == null || Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z");
        if (snapshot.Runs == null || snapshot.Runs.Length > 128) return false;
        foreach (var run in snapshot.Runs)
        {
            if (run == null || run.RunId == Guid.Empty || run.CommitId == Guid.Empty || run.OriginAttemptId == Guid.Empty ||
                run.AttemptIds == null || run.AttemptIds.Length > 10000 || run.AttemptIds.Any(x => x == Guid.Empty) ||
                run.ScanStartedAt > snapshot.ObservedAt || !Fingerprint(run.SelectionFingerprint) ||
                run.FilesSkipped < 0 || run.FilesDeferred < 0 || run.FilesFailed < 0 || run.ServerFilesFailed < 0 ||
                run.Outcome is not ("pending" or "succeeded" or "completedWithErrors" or "failed") ||
                run.ServerProcessing is not ("unknown" or "notSubmitted" or "pending" or "succeeded" or "completedWithErrors" or "failed") ||
                run.LocalCompletion is not ("pending" or "completed" or "notCompleted") || run.Cleanliness is not ("unknown" or "clean" or "degraded") ||
                (run.LocalCompletion == "completed" && run.ServerProcessing is not ("succeeded" or "completedWithErrors")) ||
                (run.Cleanliness == "clean" && (run.LocalCompletion != "completed" || run.ServerProcessing != "succeeded" ||
                    run.ScanCompleted != true || run.ScanStartedAt == null || run.SelectionFingerprint == null ||
                    run.FilesSkipped != 0 || run.FilesDeferred != 0 || run.FilesFailed != 0))) return false;
        }
        return snapshot.CurrentScan is not { } scan ||
            (scan.StartedAt >= snapshot.StartedAt && scan.StartedAt <= snapshot.ObservedAt &&
             scan.SelectionFingerprint != null && Fingerprint(scan.SelectionFingerprint) && (scan.Clean != true || scan.Completed));
    }

}

internal static class PrivateFiles
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal static void EnsureDirectory(string path)
    {
        var ancestor = Path.GetFullPath(path);
        while (ancestor != null)
        {
            RejectLink(ancestor);
            if (OperatingSystem.IsLinux() && Directory.Exists(ancestor))
            {
                var info = Stat(ancestor);
                var writable = (info.Mode & 0x12) != 0; // group/other write
                var trustedOwner = info.Uid == 0 || info.Uid == GetEffectiveUserId();
                var trustedSticky = info.Uid == 0 && (info.Mode & 0x200) != 0;
                if (!trustedOwner || (ancestor != Path.GetFullPath(path) && writable && !trustedSticky))
                    throw new UnauthorizedAccessException("Dashboard storage has an unsafe parent directory.");
            }
            ancestor = Path.GetDirectoryName(ancestor);
        }
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(path, DirectoryMode);
            File.SetUnixFileMode(path, DirectoryMode); // Also checks ownership; never trust another user's writable directory.
        }
        else throw new PlatformNotSupportedException("The local dashboard currently requires Linux private user storage.");
    }
    internal static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget != null || new DirectoryInfo(path).LinkTarget != null)
            throw new IOException("Private dashboard storage cannot be a symbolic link.");
    }
    internal static async Task<string> ReadAsync(string path)
    {
        RejectLink(path);
        if (OperatingSystem.IsLinux() && Stat(path).Uid != GetEffectiveUserId())
            throw new UnauthorizedAccessException("Dashboard file belongs to another user.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~FileMode) != 0)
            throw new UnauthorizedAccessException("Dashboard file is not user-private.");
        await using var stream = new FileStream(path, System.IO.FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
    internal static async Task ReplaceAsync(string path, string text)
    {
        RejectLink(path);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var create = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) create.UnixCreateMode = FileMode;
            await using (var stream = new FileStream(temporary, create))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await stream.WriteAsync(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStat
    {
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFd, string path, int flags, uint mask, out LinuxStat info);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    private static LinuxStat Stat(string path)
    {
        if (Statx(-100, path, 0x100, 0x7ff, out var info) != 0)
            throw new IOException("Cannot inspect private dashboard storage.");
        return info;
    }

}
