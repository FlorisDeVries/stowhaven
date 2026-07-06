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

    public ManifestManager(
        [FromKeyedServices(StateStores.Manifest)] IStateDocumentStore store,
        ILogger<ManifestManager> logger,
        TelemetryProvider telemetry)
    {
        this.store = store;
        this.logger = logger;
        this.telemetry = telemetry;
    }

    // Document types within the manifest store.
    private const string BackupRunDocument = "backupRun";
    private const string RunManifestDocument = "runManifest";
    private const string FileEntryDocument = "fileEntry";
    private const string FileVersionDocument = "fileVersion";
    private const string CommitJobDocument = "commitJob";
    private const string CommitFileProgressDocument = "commitFileProgress";

    private static string DevicePartition(Guid deviceId) => $"device:{deviceId:N}";

    private static string CommitPartition(Guid commitId) => $"commit:{commitId:N}";

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
