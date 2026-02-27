using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using FlorisDeV.Logging;

namespace FlorisDeV.BackupClient.Telemetry;

public class TelemetryProvider : IDisposable
{
    public const string SourceName = "florisdev.backup.client";
    private static readonly string SourceVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";

    public ActivitySource ActivitySource { get; }
    private readonly Meter _meter;

    public Counter<int> CountFiles { get; }
    public Counter<long> CountBackupFailures { get; }

    public Histogram<long> BackupDuration { get; }
    public Histogram<long> BackupSize { get; }

    /// <summary>
    ///   Creates OpenTelemetry resource attributes for this service.
    /// </summary>
    /// <param name="environment">The deployment environment (e.g., "Development", "Production").</param>
    /// <returns>Resource attributes configuration.</returns>
    public static OtelResourceAttributes CreateResourceAttributes(string environment) => new()
    {
        ServiceName = SourceName,
        ServiceVersion = SourceVersion,
        DeploymentEnvironment = environment
    };

    public TelemetryProvider()
    {
        ActivitySource = new ActivitySource(SourceName, SourceVersion);
        _meter = new Meter(SourceName, SourceVersion);
        
        CountFiles = _meter.CreateCounter<int>(
            "florisdev.backup.files.count", 
            unit: "files",
            description: "Number of files processed during backup operations");
        
        CountBackupFailures = _meter.CreateCounter<long>(
            "florisdev.backup.failures", 
            unit: "failures",
            description: "Number of failed backup operations");
        
        BackupDuration = _meter.CreateHistogram<long>(
            "florisdev.backup.duration", 
            unit: "ms",
            description: "Duration of backup operations in milliseconds");
        
        BackupSize = _meter.CreateHistogram<long>(
            "florisdev.backup.size", 
            unit: "bytes",
            description: "Total size of backup data in bytes");
    }

    public void Dispose()
    {
        ActivitySource?.Dispose();
        _meter?.Dispose();
    }
}