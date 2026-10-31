using System.Text.Json;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupContracts.Api.Requests;
using Moq;

namespace FlorisDeV.BackupClient.Tests.Integration;

public partial class BackupServiceIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_CleanCheckSurvivesStoppedCompanionWithoutLeakingState(bool withExistingBackup)
    {
        var target = Path.Combine(_testRoot, "private-target");
        Directory.CreateDirectory(target);
        var reporting = Path.Combine(_testRoot, "reporting");
        var options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new() { ["documents"] = target },
            Dashboard = new() { Enabled = true, ReportingDirectory = reporting },
            Encryption = new() { RecoveryPhraseFilePath = "SYNTHETIC-RECOVERY-SECRET" }
        });
        var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "state.db") }), NullLogger<BackupStateService>.Instance);
        var scanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner, _testUploader, options);
        if (withExistingBackup)
        {
            await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "SYNTHETIC-FILE-SECRET");
            Assert.Equal(BackupOutcome.Succeeded, (await service.RunAsync(default)).Outcome);
        }
        Assert.Equal(BackupOutcome.NoChanges, (await service.RunAsync(default)).Outcome);
        // Public companion reads durable reporting produced without a running companion.
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard);
        using var browser = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        var json = await browser.GetStringAsync("api/status");
        using var document = JsonDocument.Parse(json);
        var snapshot = document.RootElement.GetProperty("snapshot");
        Assert.Equal("cleanCheck", snapshot.GetProperty("result").GetString());
        Assert.Equal(1, snapshot.GetProperty("schemaVersion").GetInt32());
        Assert.NotEqual(Guid.Empty, snapshot.GetProperty("attemptId").GetGuid());
        Assert.Equal(withExistingBackup ? 1 : 0, snapshot.GetProperty("filesScanned").GetInt32());
        Assert.Equal(withExistingBackup ? "recorded" : "none", snapshot.GetProperty("serverBackupEvidence").GetString());
        Assert.True(snapshot.GetProperty("scanStartedAt").GetDateTimeOffset() <= snapshot.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal("documents", snapshot.GetProperty("targetNames")[0].GetString());
        Assert.DoesNotContain(target, json);
        foreach (var file in Directory.GetFiles(reporting, "*.json"))
        {
            var content = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("SYNTHETIC-", content);
            Assert.DoesNotContain("sas-token", content);
            Assert.DoesNotContain(target, content);
        }
    }
    [Fact]
    public async Task Dashboard_OptionalReportingFailureCannotFailACleanCheck()
    {
        var target = Path.Combine(_testRoot, "target");
        Directory.CreateDirectory(target);
        var service = CreateReportingService(target, "invalid\0reporting");
        Assert.Equal(BackupOutcome.NoChanges, (await service.RunAsync(default)).Outcome);
    }


    [Fact]
    public async Task Dashboard_OtherOutcomesAndFailedAttemptsAreNeverPresentedAsCleanChecks()
    {
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "SYNTHETIC-FILE-CONTENT");
        var options = new DashboardOptions { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "reporting") };
        var service = CreateReportingService(target, options.ReportingDirectory);
        Assert.Equal(BackupOutcome.Succeeded, (await service.RunAsync(default)).Outcome);
        await using var companion = await DashboardCompanion.StartAsync(options);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options);
        var succeeded = await browser.GetStringAsync("api/status");
        Assert.Contains("succeeded", succeeded);
        Assert.DoesNotContain("cleanCheck", succeeded);
        _mockApiClient.Setup(x => x.RegisterDevice(It.IsAny<RegisterDeviceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SYNTHETIC-SECRET https://storage.example?sig=SYNTHETIC-SAS"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(default));
        var failed = await browser.GetStringAsync("api/status");
        Assert.Contains("failed", failed);
        Assert.DoesNotContain("cleanCheck", failed);
        Assert.DoesNotContain("SYNTHETIC-", failed);
        Assert.DoesNotContain("SYNTHETIC-", await File.ReadAllTextAsync(Path.Combine(options.ReportingDirectory, "snapshot.json")));
    }

    [Fact]
    public async Task Dashboard_ConcurrentReadersSeeCompleteAtomicSnapshotsAndIgnoreInterruptedWrites()
    {
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        var options = new DashboardOptions { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "reporting") };
        var service = CreateReportingService(target, options.ReportingDirectory);
        await service.RunAsync(default);
        await using var companion = await DashboardCompanion.StartAsync(options);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options);
        // A process killed before rename can leave incomplete staging data. It must never become an observation.
        await File.WriteAllTextAsync(Path.Combine(options.ReportingDirectory, "snapshot.json.interrupted.tmp"), "{incomplete");
        var writer = Task.Run(async () => { for (var index = 0; index < 20; index++) await service.RunAsync(default); });
        var attempts = new HashSet<Guid>();
        for (var index = 0; index < 40; index++)
        {
            using var status = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
            Assert.Equal("available", status.RootElement.GetProperty("state").GetString());
            var snapshot = status.RootElement.GetProperty("snapshot");
            Assert.Contains(snapshot.GetProperty("result").GetString(), new[] { "active", "cleanCheck" });
            attempts.Add(snapshot.GetProperty("attemptId").GetGuid());
        }
        await writer;
        Assert.True(attempts.Count > 1);
        const string future = "{\"schemaVersion\":999,\"future\":\"preserve-me\"}";
        var path = Path.Combine(options.ReportingDirectory, "snapshot.json");
        await File.WriteAllTextAsync(path, future);
        Assert.Equal(BackupOutcome.NoChanges, (await service.RunAsync(default)).Outcome);
        Assert.Equal(future, await File.ReadAllTextAsync(path));
        Assert.Contains("incompatible", await browser.GetStringAsync("api/status"));
    }

    private BackupService CreateReportingService(string target, string reporting)
    {
        var options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new() { ["documents"] = target }, Dashboard = new() { Enabled = true, ReportingDirectory = reporting }
        });
        var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "dashboard-state.db") }), NullLogger<BackupStateService>.Instance);
        var scanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        return new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner, _testUploader, options);
    }

}
