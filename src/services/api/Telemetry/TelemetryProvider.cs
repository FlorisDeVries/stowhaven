using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace FlorisDeV.BackupApi.Telemetry;

/// <summary>
/// Provides OpenTelemetry tracing and metrics instrumentation for the Backup API.
/// </summary>
public class TelemetryProvider : IDisposable
{
    public const string SourceName = "florisdev.backup.api";
    private static readonly string SourceVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";

    public ActivitySource ActivitySource { get; }
    private readonly Meter _meter;

    // Counters
    public Counter<long> BackupRunsStarted { get; }
    public Counter<long> BackupRunsCommitted { get; }
    public Counter<long> BackupRunsFailed { get; }
    public Counter<long> SasUrlsGenerated { get; }
    public Counter<long> StateOperations { get; }
    public Counter<long> SecretRetrievals { get; }

    // Histograms
    public Histogram<long> OperationDuration { get; }
    public Histogram<int> SasUrlTtl { get; }

    public TelemetryProvider()
    {
        ActivitySource = new ActivitySource(SourceName, SourceVersion);
        _meter = new Meter(SourceName, SourceVersion);
        
        // Initialize counters
        BackupRunsStarted = _meter.CreateCounter<long>(
            "florisdev.backup.runs.started", 
            unit: "runs",
            description: "Number of backup runs started");
        
        BackupRunsCommitted = _meter.CreateCounter<long>(
            "florisdev.backup.runs.committed", 
            unit: "runs",
            description: "Number of backup runs successfully committed");
        
        BackupRunsFailed = _meter.CreateCounter<long>(
            "florisdev.backup.runs.failed", 
            unit: "runs",
            description: "Number of backup runs that failed");
        
        SasUrlsGenerated = _meter.CreateCounter<long>(
            "florisdev.backup.sas_urls.generated", 
            unit: "urls",
            description: "Number of SAS URLs generated");
        
        StateOperations = _meter.CreateCounter<long>(
            "florisdev.backup.state.operations", 
            unit: "operations",
            description: "Number of state store operations performed");
        
        SecretRetrievals = _meter.CreateCounter<long>(
            "florisdev.backup.secrets.retrievals", 
            unit: "retrievals",
            description: "Number of secret retrievals from secret store");
        
        // Initialize histograms
        OperationDuration = _meter.CreateHistogram<long>(
            "florisdev.backup.operation.duration", 
            unit: "ms",
            description: "Duration of operations in milliseconds");
        
        SasUrlTtl = _meter.CreateHistogram<int>(
            "florisdev.backup.sas_url.ttl", 
            unit: "minutes",
            description: "TTL of generated SAS URLs in minutes");
    }

    public void Dispose()
    {
        ActivitySource?.Dispose();
        _meter?.Dispose();
    }
}
