using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlorisDeV.BackupClient.Models;

// Portable evidence only. This contract deliberately has no state, credentials, paths or exception text.
public sealed record BackupRunResult
{
    public Guid RunId { get; init; }
    public Guid? CommitId { get; init; }
    public Guid? OriginAttemptId { get; init; }
    public Guid[] AttemptIds { get; init; } = [];
    public DateTimeOffset? ScanStartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? ServerCompletedAt { get; init; }
    public DateTimeOffset? ServerCompletionObservedAt { get; init; }
    public string? SelectionFingerprint { get; init; }
    public int? FilesSkipped { get; init; }
    public int? FilesDeferred { get; init; }
    public int? FilesFailed { get; init; }
    public int? ServerFilesFailed { get; init; }
    public bool? ScanCompleted { get; init; }
    [JsonConverter(typeof(BackupStatusJsonConverter<BackupRunOutcome>))]
    public BackupRunOutcome Outcome { get; init; } = BackupRunOutcome.Pending;
    [JsonConverter(typeof(BackupStatusJsonConverter<BackupServerProcessing>))]
    public BackupServerProcessing ServerProcessing { get; init; } = BackupServerProcessing.Unknown;
    [JsonConverter(typeof(BackupStatusJsonConverter<BackupLocalCompletion>))]
    public BackupLocalCompletion LocalCompletion { get; init; } = BackupLocalCompletion.Pending;
    [JsonConverter(typeof(BackupStatusJsonConverter<BackupCleanliness>))]
    public BackupCleanliness Cleanliness { get; init; } = BackupCleanliness.Unknown;
    internal bool HasCompletedServer => ServerProcessing is BackupServerProcessing.Succeeded or BackupServerProcessing.CompletedWithErrors;
    internal bool HasCleanScan => ScanCompleted == true && ScanStartedAt != null && SelectionFingerprint != null &&
        FilesSkipped == 0 && FilesDeferred == 0 && FilesFailed == 0;
    internal bool HasValidStatuses => Enum.IsDefined(Outcome) && Enum.IsDefined(ServerProcessing) &&
        Enum.IsDefined(LocalCompletion) && Enum.IsDefined(Cleanliness) &&
        (LocalCompletion != BackupLocalCompletion.Completed || HasCompletedServer) &&
        (Cleanliness != BackupCleanliness.Clean || LocalCompletion == BackupLocalCompletion.Completed &&
            ServerProcessing == BackupServerProcessing.Succeeded && HasCleanScan);
}

public sealed record BackupScanEvidence(DateTimeOffset StartedAt, string SelectionFingerprint,
    bool Completed, bool? Clean, bool NoChanges);

[JsonConverter(typeof(BackupStatusJsonConverter<BackupRunOutcome>))]
public enum BackupRunOutcome { Pending, Succeeded, CompletedWithErrors, Failed }
[JsonConverter(typeof(BackupStatusJsonConverter<BackupServerProcessing>))]
public enum BackupServerProcessing { Unknown, NotSubmitted, Pending, Succeeded, CompletedWithErrors, Failed }
[JsonConverter(typeof(BackupStatusJsonConverter<BackupLocalCompletion>))]
public enum BackupLocalCompletion { Pending, Completed, NotCompleted }
[JsonConverter(typeof(BackupStatusJsonConverter<BackupCleanliness>))]
public enum BackupCleanliness { Unknown, Clean, Degraded }

// The portable version-one contract accepts exact names only, never numbers or future values.
public sealed class BackupStatusJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly IReadOnlyDictionary<T, string> Names = Enum.GetValues<T>()
        .ToDictionary(value => value, value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            foreach (var (value, name) in Names)
                if (reader.GetString() == name) return value;
        throw new JsonException("Unsupported backup run status.");
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Names.TryGetValue(value, out var name)) throw new JsonException("Unsupported backup run status.");
        writer.WriteStringValue(name);
    }
}
