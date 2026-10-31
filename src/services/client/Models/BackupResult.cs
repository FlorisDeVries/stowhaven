using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlorisDeV.BackupClient.Models;

public enum BackupOutcome { NoChanges, Succeeded, CompletedWithErrors, Pending, Failed }

public sealed record BackupResult
{
    public required BackupOutcome Outcome { get; init; }
    public Guid AttemptId { get; init; }
    public BackupRunResult[] Runs { get; init; } = [];
    public BackupScanEvidence? CurrentScan { get; init; }
    public string Operation { get; init; } = "backup";
    public Guid? DeviceId { get; init; }
    public Guid? RunId { get; init; }
    public Guid? CommitId { get; init; }
    public int FilesScanned { get; init; }
    public int FilesUploaded { get; init; }
    public int FilesSkipped { get; init; }
    public int FilesFailed { get; init; }
    public int FilesDeferred { get; init; }
    public string? ErrorType { get; init; }
    public DateTimeOffset ReportedAt { get; init; } = DateTimeOffset.UtcNow;
    public int ExitCode => Outcome switch { BackupOutcome.Failed => 1, BackupOutcome.Pending => 2, BackupOutcome.CompletedWithErrors => 3, _ => 0 };

    public async Task WriteAsync(string? path, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }, WriteIndented = true };
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, jsonOptions), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
