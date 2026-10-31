using FlorisDeV.BackupClient.Models;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Services;

namespace FlorisDeV.BackupClient.Dashboard;

// Independent of bounded attempt history. These are observations, never backup state.
public sealed record DashboardCleanEvidence(Guid AttemptId, Guid? RunId, DateTimeOffset ScanStartedAt,
    DateTimeOffset CompletedAt, string SelectionFingerprint, string Outcome);
public sealed record DashboardCompletedBackupEvidence(Guid AttemptId, Guid RunId, DateTimeOffset? ScanStartedAt,
    DateTimeOffset? CompletedAt, string? SelectionFingerprint, BackupServerProcessing Outcome, BackupCleanliness Cleanliness)
{
    public DateTimeOffset? ServerCompletedAt { get; init; }
    public DateTimeOffset ServerCompletionObservedAt { get; init; }
    public BackupLocalCompletion LocalCompletion { get; init; } = BackupLocalCompletion.Pending;
}
public sealed record DashboardBackupEvidence(int SchemaVersion = 1)
{
    public DashboardCleanEvidence? LastCleanResult { get; init; }
    public DashboardCleanEvidence? LastCleanCheck { get; init; }
    public DashboardCompletedBackupEvidence? LastCompletedServerBackup { get; init; }
}
public sealed record DashboardFreshness(string State, double? AgeSeconds, double? MaximumAgeSeconds, string Message);

internal sealed class DashboardReportingCompatibilityException() : InvalidOperationException("Reporting schema is unsupported. Upgrade the client and dashboard together; preserve the reporting files.");

internal static class DashboardFreshnessReporting
{
    internal static string Selection(BackupClientOptions options, string? clientBaseDirectory = null)
    {
        var ignore = options.IgnoreFilePath;
        if (!string.IsNullOrWhiteSpace(ignore) && !Path.IsPathRooted(ignore) && clientBaseDirectory != null)
            ignore = Path.Combine(clientBaseDirectory, ignore);
        return DashboardReporting.Fingerprint(options.GetEffectiveTargets(), BackupIgnoreParser.GetCombinedPatterns(ignore), clientBaseDirectory);
    }

    internal static DashboardFreshness Project(DashboardBackupEvidence? evidence, DashboardOptions options,
        string? currentSelection, DateTimeOffset now)
    {
        var limit = options.MaximumBackupAge;
        var maximum = limit is { } age && age > TimeSpan.Zero ? age.TotalSeconds : (double?)null;
        var clean = evidence?.LastCleanResult;
        if (clean == null) return new("unknown", null, maximum, "No proven clean result has been recorded.");
        var seconds = Math.Max(0, (now - clean.ScanStartedAt).TotalSeconds);
        if (currentSelection != null && currentSelection != clean.SelectionFingerprint)
            return new("configurationChanged", seconds, maximum, "Backup configuration changed. A clean result covering the current selection is required.");
        if (maximum == null) return new("unconfigured", seconds, null, "Maximum backup age is not configured; age is shown without a freshness judgment.");
        if (currentSelection == null) return new("unknown", seconds, maximum, "Current backup selection cannot be read; freshness is unknown.");
        return seconds > maximum ? new("overdue", seconds, maximum, "The last clean result is overdue.")
            : new("fresh", seconds, maximum, "The current selection has a recent clean result.");
    }

    internal static bool Valid(DashboardBackupEvidence? evidence)
    {
        static bool Entry(DashboardCleanEvidence? item, bool check) => item == null ||
            (item.AttemptId != Guid.Empty && item.RunId != Guid.Empty && item.ScanStartedAt != default &&
             item.CompletedAt >= item.ScanStartedAt && Regex.IsMatch(item.SelectionFingerprint ?? "", "\\A[a-f0-9]{64}\\z") &&
             (check ? item.Outcome == "cleanCheck" : item.Outcome is "cleanCheck" or "succeeded") &&
             (item.Outcome == "cleanCheck" ? item.RunId == null : item.RunId != null));
        var backup = evidence?.LastCompletedServerBackup;
        var validBackup = backup == null || backup.AttemptId != Guid.Empty && backup.RunId != Guid.Empty &&
            backup.ServerCompletionObservedAt != default && (backup.ScanStartedAt == null || backup.ServerCompletionObservedAt >= backup.ScanStartedAt) &&
            backup.LocalCompletion is BackupLocalCompletion.Pending or BackupLocalCompletion.Completed &&
            (backup.LocalCompletion == BackupLocalCompletion.Completed ? backup.CompletedAt != null && (backup.ScanStartedAt == null || backup.CompletedAt >= backup.ScanStartedAt) : backup.CompletedAt == null) &&
            (backup.SelectionFingerprint == null || Regex.IsMatch(backup.SelectionFingerprint, "\\A[a-f0-9]{64}\\z")) &&
            backup.Outcome is BackupServerProcessing.Succeeded or BackupServerProcessing.CompletedWithErrors && Enum.IsDefined(backup.Cleanliness);
        return evidence == null || evidence.SchemaVersion == 1 && Entry(evidence.LastCleanResult, false) &&
            Entry(evidence.LastCleanCheck, true) && validBackup;
    }

    internal static async Task<DashboardBackupEvidence?> ReadAsync(DashboardOptions options)
    {
        var path = Path.Combine(options.ReportingDirectory, "clean-evidence.json");
        if (!File.Exists(path)) return null;
        var text = await PrivateFiles.ReadAsync(path);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schema) || schema != 1)
            throw new DashboardReportingCompatibilityException();
        var value = JsonSerializer.Deserialize<DashboardBackupEvidence>(text, DashboardReporting.Json);
        if (value == null || !Valid(value)) throw new InvalidOperationException("Invalid clean evidence.");
        return value;
    }

    internal static async Task<DashboardSnapshot> RecordAsync(DashboardOptions options, DashboardSnapshot snapshot)
    {
        var previous = await ReadAsync(options) ?? snapshot.Evidence ?? new();
        var evidence = previous;
        static DashboardCleanEvidence? Newer(DashboardCleanEvidence? old, DashboardCleanEvidence candidate)
            => old == null || candidate.ScanStartedAt > old.ScanStartedAt ||
                candidate.ScanStartedAt == old.ScanStartedAt && candidate.CompletedAt >= old.CompletedAt ? candidate : old;
        foreach (var run in snapshot.Runs.Where(r => r.HasCompletedServer &&
            r.ServerCompletionObservedAt != null))
        {
            var completed = new DashboardCompletedBackupEvidence(run.OriginAttemptId ?? snapshot.AttemptId, run.RunId,
                run.ScanStartedAt, run.CompletedAt, run.SelectionFingerprint, run.ServerProcessing, run.Cleanliness)
            { LocalCompletion = run.LocalCompletion, ServerCompletedAt = run.ServerCompletedAt,
                ServerCompletionObservedAt = run.ServerCompletionObservedAt!.Value };
            if (evidence.LastCompletedServerBackup == null || completed.ServerCompletionObservedAt > evidence.LastCompletedServerBackup.ServerCompletionObservedAt ||
                completed.ServerCompletionObservedAt == evidence.LastCompletedServerBackup.ServerCompletionObservedAt &&
                (completed.ScanStartedAt ?? DateTimeOffset.MinValue) >= (evidence.LastCompletedServerBackup.ScanStartedAt ?? DateTimeOffset.MinValue))
                evidence = evidence with { LastCompletedServerBackup = completed };
            if (run.Cleanliness == BackupCleanliness.Clean && run.LocalCompletion == BackupLocalCompletion.Completed && run.CompletedAt != null)
            {
                var item = new DashboardCleanEvidence(completed.AttemptId, run.RunId, run.ScanStartedAt!.Value,
                    run.CompletedAt.Value, run.SelectionFingerprint!, "succeeded");
                evidence = evidence with { LastCleanResult = Newer(evidence.LastCleanResult, item) };
            }
        }
        if (snapshot.Result != "active" && snapshot.CurrentScan is { Completed: true, Clean: true, NoChanges: true } scan)
        {
            var item = new DashboardCleanEvidence(snapshot.AttemptId, null, scan.StartedAt, snapshot.ObservedAt, scan.SelectionFingerprint, "cleanCheck");
            evidence = evidence with { LastCleanCheck = Newer(evidence.LastCleanCheck, item), LastCleanResult = Newer(evidence.LastCleanResult, item) };
        }
        if (evidence != previous)
            await PrivateFiles.ReplaceAsync(Path.Combine(options.ReportingDirectory, "clean-evidence.json"), JsonSerializer.Serialize(evidence, DashboardReporting.Json));
        return snapshot with { Evidence = evidence,
            ServerBackupEvidence = evidence.LastCompletedServerBackup != null ? "recorded" : snapshot.ServerBackupEvidence };
    }
}
