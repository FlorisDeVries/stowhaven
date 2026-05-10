using Dapr.Client;
using FlorisDeV.BackupApi.Telemetry;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager : IManifestManager
{
    private readonly DaprClient daprClient;
    private readonly ILogger<ManifestManager> logger;
    private readonly TelemetryProvider telemetry;

    public ManifestManager(
        DaprClient daprClient,
        ILogger<ManifestManager> logger,
        TelemetryProvider telemetry)
    {
        this.daprClient = daprClient;
        this.logger = logger;
        this.telemetry = telemetry;
    }
}
