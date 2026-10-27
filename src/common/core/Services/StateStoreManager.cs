using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupApi.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager : IManifestManager
{
    private readonly IStateDocumentStore store;
    private readonly ILogger<ManifestManager> logger;
    private readonly TelemetryProvider telemetry;
    private readonly TimeSpan _commitLeaseDuration;

    public ManifestManager(
        [FromKeyedServices(StateStores.Manifest)] IStateDocumentStore store,
        ILogger<ManifestManager> logger,
        TelemetryProvider telemetry,
        IConfiguration? configuration = null)
    {
        this.store = store;
        this.logger = logger;
        this.telemetry = telemetry;
        _commitLeaseDuration = TimeSpan.FromSeconds(Math.Clamp(
            configuration?.GetValue("CommitProcessing:LeaseDurationSeconds", 120) ?? 120, 5, 3600));
    }

    // Document types within the manifest store.
    private const string BackupRunDocument = "backupRun";
    private const string RunManifestDocument = "runManifest";
    private const string RunManifestChunkDocument = "runManifestChunk";
    private const string FileEntryDocument = "fileEntry";
    private const string FileVersionDocument = "fileVersion";
    private const string CommitJobDocument = "commitJob";
    private const string CommitFileProgressDocument = "commitFileProgress";

    private static string DevicePartition(Guid deviceId) => $"device:{deviceId:N}";

    private static string CommitPartition(Guid commitId) => $"commit:{commitId:N}";

    // Run-scoped partition isolating a single run's manifest chunks, so reassembly is a
    // partition scan ordered by sort key (mirrors the CommitFileProgress access pattern and
    // needs no composite index).
    private static string RunManifestPartition(Guid deviceId, Guid runId) => $"run:{deviceId:N}:{runId:N}";

    private static string EncodeStateKeySegment(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static Guid CreateDeterministicCommitId(Guid deviceId, Guid runId)
    {
        var input = Encoding.UTF8.GetBytes($"{deviceId:N}:{runId:N}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        return new Guid(guidBytes);
    }

    private static string FormatGuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
}
