using System.Net.Http.Json;
using System.Text.Json;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FlorisDeV.BackupContracts.Api.Requests;
using Moq;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupClient.Tests.Integration;

public partial class BackupServiceIntegrationTests
{
    [Fact]
    public async Task Dashboard_FreshnessEmptyFirstCleanCheckHasAgeWithoutInventingPolicyOrServerBackup()
    {
        var target = Path.Combine(_testRoot, "fresh-target"); Directory.CreateDirectory(target);
        var ignore = Path.Combine(_testRoot, "ignore"); await File.WriteAllTextAsync(ignore, "*.tmp\n");
        var excluded = Path.Combine(target, "excluded.tmp"); await File.WriteAllTextAsync(excluded, "synthetic");
        using var excludedLock = new FileStream(excluded, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var options = new BackupClientOptions { BackupTargets = new() { ["documents"] = target }, IgnoreFilePath = ignore,
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "fresh-reporting") } };
        var clock = new LiveClock();
        var service = CreateFreshnessService(options, clock);
        Assert.Equal(BackupOutcome.NoChanges, (await service.RunAsync(default)).Outcome);
        clock.AdvanceWithoutTimers(TimeSpan.FromHours(2));
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, clock);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Dashboard);
        using var json = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
        var freshness = json.RootElement.GetProperty("freshness");
        Assert.Equal("unconfigured", freshness.GetProperty("state").GetString());
        Assert.Equal(7200, freshness.GetProperty("ageSeconds").GetDouble());
        var evidence = json.RootElement.GetProperty("snapshot").GetProperty("evidence");
        Assert.Equal("cleanCheck", evidence.GetProperty("lastCleanResult").GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("lastCompletedServerBackup").ValueKind);
        Assert.Equal("none", json.RootElement.GetProperty("snapshot").GetProperty("serverBackupEvidence").GetString());
    }

    [Fact]
    public async Task Dashboard_FreshnessRetainsCleanEvidenceAfterFailureAndDetectsCurrentSelectionEditsWithoutRestart()
    {
        var target = Path.Combine(_testRoot, "fresh-target"); Directory.CreateDirectory(target);
        var globalIgnore = Path.Combine(_testRoot, "global.ignore");
        await File.WriteAllTextAsync(globalIgnore, "*.tmp\n");
        var options = new BackupClientOptions { BackupTargets = new() { ["documents"] = target }, IgnoreFilePath = globalIgnore,
            Dashboard = new() { Enabled = true, MaximumBackupAge = TimeSpan.FromHours(1), ReportingDirectory = Path.Combine(_testRoot, "fresh-reporting") } };
        var clock = new LiveClock();
        var service = CreateFreshnessService(options, clock);
        await service.RunAsync(default);
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, clock, currentOptions: () => options);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Dashboard);
        async Task<JsonElement> Status() { using var doc = JsonDocument.Parse(await browser.GetStringAsync("api/status")); return doc.RootElement.Clone(); }
        Assert.Equal("fresh", (await Status()).GetProperty("freshness").GetProperty("state").GetString());
        var clean = (await Status()).GetProperty("snapshot").GetProperty("evidence").GetProperty("lastCleanResult").Clone();
        // Latest observations may be replaced/pruned. The independent clean baseline remains usable.
        File.Delete(Path.Combine(options.Dashboard.ReportingDirectory, "snapshot.json"));
        clock.AdvanceWithoutTimers(TimeSpan.FromHours(2));
        _mockApiClient.Setup(x => x.RegisterDevice(It.IsAny<RegisterDeviceRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("synthetic"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(default));
        var failed = await Status();
        Assert.Equal("failed", failed.GetProperty("snapshot").GetProperty("result").GetString());
        Assert.Equal("overdue", failed.GetProperty("freshness").GetProperty("state").GetString());
        Assert.Equal(clean.ToString(), failed.GetProperty("snapshot").GetProperty("evidence").GetProperty("lastCleanResult").ToString());
        await File.WriteAllTextAsync(globalIgnore, "*.log\n");
        Assert.Equal("configurationChanged", (await Status()).GetProperty("freshness").GetProperty("state").GetString());
        await File.WriteAllTextAsync(Path.Combine(target, ".backupignore"), "*.log\n");
        await File.WriteAllTextAsync(globalIgnore, "*.tmp\n");
        Assert.Equal("configurationChanged", (await Status()).GetProperty("freshness").GetProperty("state").GetString());
        // A local ignore file overrides the global rules. Editing unused global rules is not a selection change.
        SetupMockApiClient();
        await service.RunAsync(default);
        Assert.Equal("fresh", (await Status()).GetProperty("freshness").GetProperty("state").GetString());
        await File.WriteAllTextAsync(globalIgnore, "unused-global-rule\n");
        Assert.Equal("fresh", (await Status()).GetProperty("freshness").GetProperty("state").GetString());
        var other = Path.Combine(_testRoot, "other-target"); Directory.CreateDirectory(other);
        options.BackupTargets["documents"] = other;
        Assert.Equal("configurationChanged", (await Status()).GetProperty("freshness").GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_FreshnessPendingCompletionKeepsOriginalScanAgeAndIndependentCleanNewRun(bool degradedOriginal)
    {
        var target = Path.Combine(_testRoot, "fresh-target"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "first.txt"), "first");
        var lockedPath = Path.Combine(target, "locked.txt");
        if (degradedOriginal) await File.WriteAllTextAsync(lockedPath, "locked");
        FileStream? locked = degradedOriginal ? new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        try
        {
            var options = new BackupClientOptions { BackupTargets = new() { ["documents"] = target }, CommitStatusTimeoutSeconds = 0,
                Dashboard = new() { Enabled = true, MaximumBackupAge = TimeSpan.FromHours(1), ReportingDirectory = Path.Combine(_testRoot, "fresh-reporting") } };
            var clock = new LiveClock();
            var complete = false;
            Guid originalRun = default;
            var originalServerCompletion = clock.GetUtcNow().AddMinutes(30);
            _mockApiClient.Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid device, Guid commit, CancellationToken _) => new CommitStatusResponse
                { DeviceId = device, RunId = _committedRuns[commit], CommitId = commit, Status = complete ? CommitJobStatus.Succeeded : CommitJobStatus.Processing,
                    CompletedAt = complete ? _committedRuns[commit] == originalRun ? originalServerCompletion : clock.GetUtcNow() : null });
            var service = CreateFreshnessService(options, clock);
            var first = await service.RunAsync(default);
            Assert.Equal(BackupOutcome.Pending, first.Outcome);
            var originalScan = first.CurrentScan!.StartedAt;
            originalRun = first.RunId!.Value;
            await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, clock, currentOptions: () => options);
            using var browser = new HttpClient { BaseAddress = companion.Address };
            await DashboardTestAuthorization.AuthorizeAsync(browser, options.Dashboard);
            using (var pending = JsonDocument.Parse(await browser.GetStringAsync("api/status")))
                Assert.Equal("unknown", pending.RootElement.GetProperty("freshness").GetProperty("state").GetString());
            clock.AdvanceWithoutTimers(TimeSpan.FromHours(2));
            if (degradedOriginal) { locked!.Dispose(); locked = null; }
            else
            {
                await File.WriteAllTextAsync(lockedPath, "locked");
                locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            complete = true;
            var finished = await service.RunAsync(default);
            Assert.Equal(BackupOutcome.CompletedWithErrors, finished.Outcome);
            using var json = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
            var freshness = json.RootElement.GetProperty("freshness");
            var evidence = json.RootElement.GetProperty("snapshot").GetProperty("evidence");
            Assert.Equal(degradedOriginal ? "fresh" : "overdue", freshness.GetProperty("state").GetString());
            Assert.Equal(degradedOriginal ? 0 : 7200, freshness.GetProperty("ageSeconds").GetDouble());
            Assert.Equal(degradedOriginal ? clock.GetUtcNow() : originalScan,
                evidence.GetProperty("lastCleanResult").GetProperty("scanStartedAt").GetDateTimeOffset());
            Assert.Equal(clock.GetUtcNow(), evidence.GetProperty("lastCleanResult").GetProperty("completedAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, evidence.GetProperty("lastCleanCheck").ValueKind);
            Assert.Equal("succeeded", evidence.GetProperty("lastCompletedServerBackup").GetProperty("outcome").GetString());
            Assert.Equal(degradedOriginal ? clock.GetUtcNow() : originalServerCompletion,
                evidence.GetProperty("lastCompletedServerBackup").GetProperty("serverCompletedAt").GetDateTimeOffset());
            Assert.Equal(clock.GetUtcNow(), evidence.GetProperty("lastCompletedServerBackup").GetProperty("serverCompletionObservedAt").GetDateTimeOffset());
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public async Task Dashboard_FreshnessLegacyPendingCompletionShowsKnownBackupWithoutInventingCleanliness()
    {
        var target = Path.Combine(_testRoot, "fresh-target"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "first.txt"), "first");
        var options = new BackupClientOptions { BackupTargets = new() { ["documents"] = target }, CommitStatusTimeoutSeconds = 0,
            Dashboard = new() { Enabled = true, MaximumBackupAge = TimeSpan.FromHours(1), ReportingDirectory = Path.Combine(_testRoot, "fresh-reporting") } };
        var clock = new LiveClock();
        var complete = false;
        _mockApiClient.Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid device, Guid commit, CancellationToken _) => new CommitStatusResponse
            { DeviceId = device, RunId = _committedRuns[commit], CommitId = commit, Status = complete ? CommitJobStatus.Succeeded : CommitJobStatus.Processing });
        var service = CreateFreshnessService(options, clock);
        var pending = await service.RunAsync(default);
        using var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "fresh-state.db") }), NullLogger<BackupStateService>.Instance);
        var header = (await state.GetPendingBackupRunAsync(pending.DeviceId!.Value))!;
        await state.SavePendingBackupRunAsync(header with { Evidence = null }); // An older client had no scan evidence.
        var lockedPath = Path.Combine(target, "locked.txt");
        await File.WriteAllTextAsync(lockedPath, "locked");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        complete = true;
        var result = await service.RunAsync(default);
        Assert.Equal(BackupOutcome.CompletedWithErrors, result.Outcome);
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, clock, currentOptions: () => options);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Dashboard);
        using var json = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
        Assert.Equal("unknown", json.RootElement.GetProperty("freshness").GetProperty("state").GetString());
        var evidence = json.RootElement.GetProperty("snapshot").GetProperty("evidence");
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("lastCleanResult").ValueKind);
        Assert.Equal("unknown", evidence.GetProperty("lastCompletedServerBackup").GetProperty("cleanliness").GetString());
        Assert.Equal(header.RunId, evidence.GetProperty("lastCompletedServerBackup").GetProperty("runId").GetGuid());
    }

    [Fact]
    public async Task Dashboard_FreshnessLegacySuccessTimestampAndLaterDegradedAttemptRemainUnknown()
    {
        var target = Path.Combine(_testRoot, "fresh-target"); Directory.CreateDirectory(target);
        var path = Path.Combine(target, "first.txt"); await File.WriteAllTextAsync(path, "first");
        var clock = new LiveClock();
        Assert.Equal(BackupOutcome.Succeeded, (await CreateFreshnessService(new BackupClientOptions
            { BackupTargets = new() { ["documents"] = target } }, clock).RunAsync(default)).Outcome);
        await File.WriteAllTextAsync(path, "changed after legacy backup");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var options = new BackupClientOptions { BackupTargets = new() { ["documents"] = target },
            Dashboard = new() { Enabled = true, MaximumBackupAge = TimeSpan.FromHours(1), ReportingDirectory = Path.Combine(_testRoot, "fresh-reporting") } };
        Assert.Equal(BackupOutcome.CompletedWithErrors, (await CreateFreshnessService(options, clock).RunAsync(default)).Outcome);
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, clock, currentOptions: () => options);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Dashboard);
        using var json = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
        Assert.Equal("unknown", json.RootElement.GetProperty("freshness").GetProperty("state").GetString());
        Assert.Equal("recorded", json.RootElement.GetProperty("snapshot").GetProperty("serverBackupEvidence").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("snapshot").GetProperty("evidence").GetProperty("lastCleanResult").ValueKind);
    }

    [Fact]
    public async Task Dashboard_FreshnessCompletedWithErrorsKeepsPriorCleanBaselineAndShowsLatestCompletedBackup()
    {
        var target = Path.Combine(_testRoot, "fresh-target"); Directory.CreateDirectory(target);
        var path = Path.Combine(target, "first.txt"); await File.WriteAllTextAsync(path, "first");
        var options = new BackupClientOptions { BackupTargets = new() { ["documents"] = target },
            Dashboard = new() { Enabled = true, MaximumBackupAge = TimeSpan.FromHours(1), ReportingDirectory = Path.Combine(_testRoot, "fresh-reporting") } };
        var clock = new LiveClock();
        var service = CreateFreshnessService(options, clock);
        var clean = await service.RunAsync(default);
        clock.AdvanceWithoutTimers(TimeSpan.FromHours(2));
        await File.WriteAllTextAsync(path, "new content");
        _mockApiClient.Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid device, Guid commit, CancellationToken _) => new CommitStatusResponse
            { DeviceId = device, RunId = _committedRuns[commit], CommitId = commit,
                Status = commit == clean.CommitId ? CommitJobStatus.Succeeded : CommitJobStatus.CompletedWithErrors, FilesFailed = 0 });
        _mockApiClient.Setup(x => x.ListFailedCommitFiles(It.IsAny<Guid>(), It.IsAny<Guid>(), 500, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid device, Guid commit, int _, string? _, CancellationToken _) => new ListCommitFileProgressResponse
                { DeviceId = device, RunId = _committedRuns[commit], CommitId = commit, PageSize = 500, Files = [] });
        var degraded = await service.RunAsync(default);
        Assert.Equal(BackupOutcome.CompletedWithErrors, degraded.Outcome);
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, clock, currentOptions: () => options);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Dashboard);
        using var json = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
        Assert.Equal("overdue", json.RootElement.GetProperty("freshness").GetProperty("state").GetString());
        var evidence = json.RootElement.GetProperty("snapshot").GetProperty("evidence");
        Assert.Equal(clean.RunId, evidence.GetProperty("lastCleanResult").GetProperty("runId").GetGuid());
        Assert.Equal(degraded.RunId, evidence.GetProperty("lastCompletedServerBackup").GetProperty("runId").GetGuid());
        Assert.Equal("completedWithErrors", evidence.GetProperty("lastCompletedServerBackup").GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("{\"schemaVersion\":999,\"future\":\"preserve-me\"}", false)]
    [InlineData("{\"schemaVersion\":999,\"future\":\"preserve-me\"}", true)]
    [InlineData("{\"lastCleanResult\":null}", false)]
    [InlineData("{\"lastCleanResult\":null}", true)]
    public async Task Dashboard_FreshnessUnsupportedBaselineIsPreservedAndCannotFailBackup(string unsupported, bool snapshotMissing)
    {
        var target = Path.Combine(_testRoot, "fresh-target"); Directory.CreateDirectory(target);
        var options = new BackupClientOptions { BackupTargets = new() { ["documents"] = target },
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "fresh-reporting") } };
        var clock = new LiveClock();
        var service = CreateFreshnessService(options, clock);
        await service.RunAsync(default);
        var baseline = Path.Combine(options.Dashboard.ReportingDirectory, "clean-evidence.json");
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, clock);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Dashboard);
        var valid = await browser.GetFromJsonAsync<DashboardStatus>("api/status");
        Assert.NotNull(valid!.Snapshot!.Evidence!.LastCleanResult);
        await File.WriteAllTextAsync(baseline, unsupported);
        if (snapshotMissing) File.Delete(Path.Combine(options.Dashboard.ReportingDirectory, "snapshot.json"));
        Assert.Equal(BackupOutcome.NoChanges, (await service.RunAsync(default)).Outcome);
        Assert.Equal(unsupported, await File.ReadAllTextAsync(baseline));
        if (snapshotMissing) Assert.False(File.Exists(Path.Combine(options.Dashboard.ReportingDirectory, "snapshot.json")));
        var incompatible = await browser.GetFromJsonAsync<DashboardStatus>("api/status");
        Assert.Equal("incompatible", incompatible!.State);
        Assert.Contains("Upgrade", incompatible.Message);
        Assert.Equal(valid.Snapshot.AttemptId, incompatible.Snapshot!.AttemptId);
        Assert.Equal(valid.Snapshot.Evidence.LastCleanResult, incompatible.Snapshot.Evidence!.LastCleanResult);
    }

    [Fact]
    public async Task Dashboard_ContinuousObservationCannotPreventSequentialBackupAttempts()
    {
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        var options = new BackupClientOptions { BackupTargets = new() { ["documents"] = target } };
        var service = CreateFreshnessService(options, TimeProvider.System);
        Assert.Equal(BackupOutcome.NoChanges, (await service.RunAsync(default)).Outcome);
        var dashboard = new DashboardOptions { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "observations") };
        var systemd = new Mock<ISystemdUserService>();
        systemd.Setup(x => x.ShowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SystemdControlException("schedulerUnavailable", "Synthetic manager boundary."));
        await using var companion = await DashboardCompanion.StartAsync(dashboard,
            startControl: new(new() { FilePath = Path.Combine(_testRoot, "fresh-state.db") }, options, systemd.Object));
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, dashboard);
        using var stop = new CancellationTokenSource();
        var reads = 0;
        var observers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await browser.GetStringAsync("api/status", stop.Token);
                    Interlocked.Increment(ref reads);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        })).ToArray();
        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Volatile.Read(ref reads) < 16) await Task.Delay(1, startupTimeout.Token);
            for (var attempt = 0; attempt < 200; attempt++)
                Assert.Equal(BackupOutcome.NoChanges, (await service.RunAsync(default)).Outcome);
        }
        finally { stop.Cancel(); await Task.WhenAll(observers); }
    }

    private BackupService CreateFreshnessService(BackupClientOptions options, TimeProvider clock)
    {
        var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "fresh-state.db") }), NullLogger<BackupStateService>.Instance);
        var scanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        return new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner, _testUploader, Options.Create(options), clock);
    }
}
