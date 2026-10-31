using System.Text.Json;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FlorisDeV.BackupClient.Tests.Integration;

public partial class BackupServiceIntegrationTests
{
    [Fact]
    public async Task Dashboard_RecentRedactedLogsCanBeOpenedDuringActiveAndCompletedAttempt()
    {
        var target = Path.Combine(_testRoot, "log-target");
        Directory.CreateDirectory(target);
        var dashboard = new DashboardOptions
        {
            Enabled = true,
            ReportingDirectory = Path.Combine(_testRoot, "log-reporting"),
            LogDirectory = Path.Combine(_testRoot, "client-logs")
        };
        Directory.CreateDirectory(dashboard.LogDirectory);
        var logPath = Path.Combine(dashboard.LogDirectory, "backup-client-20261031.log");
        await File.WriteAllTextAsync(logPath,
            "2026-10-31 12:00:00.000 +00:00 [INF] Waiting for backup API\n" +
            "2026-10-31 12:00:00.001 +00:00 [DBG] Bearer SYNTHETIC-DEMO-SECRET\n");
        var options = Options.Create(new BackupClientOptions { BackupTargets = new() { ["documents"] = target }, Dashboard = dashboard });
        Guid device = default;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DeviceRegistrationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockApiClient.Setup(x => x.RegisterDevice(It.IsAny<RegisterDeviceRequest>(), It.IsAny<CancellationToken>()))
            .Returns((RegisterDeviceRequest request, CancellationToken _) => { device = request.DeviceId!.Value; entered.SetResult(); return release.Task; });
        var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "log-state.db") }), NullLogger<BackupStateService>.Instance);
        var scanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner, _testUploader, options);
        var attempt = service.RunAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var companion = await DashboardCompanion.StartAsync(dashboard);
            using var browser = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = companion.Address };
            await DashboardTestAuthorization.AuthorizeAsync(browser, dashboard);
            Assert.Contains("Open recent logs", await browser.GetStringAsync("/"));
            var status = JsonDocument.Parse(await browser.GetStringAsync("api/status")).RootElement;
            Assert.True(status.GetProperty("snapshot").GetProperty("live").GetProperty("active").GetBoolean());
            var logs = await browser.GetStringAsync("api/logs");
            Assert.Contains("Waiting for backup API", logs);
            Assert.DoesNotContain("SYNTHETIC-DEMO-SECRET", logs);
            release.SetResult(new DeviceRegistrationResponse { DeviceId = device, TenantId = "test-tenant", UserId = "test-user" });
            Assert.Equal(BackupOutcome.NoChanges, (await attempt.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
            await File.AppendAllTextAsync(logPath, "2026-10-31 12:00:01.000 +00:00 [INF] Completed check: no changes\n");
            logs = await browser.GetStringAsync("api/logs");
            Assert.Contains("Completed check: no changes", logs);
            Assert.DoesNotContain("SYNTHETIC-DEMO-SECRET", logs);
        }
        finally
        {
            release.TrySetResult(new DeviceRegistrationResponse { DeviceId = device, TenantId = "test-tenant", UserId = "test-user" });
            await attempt.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
