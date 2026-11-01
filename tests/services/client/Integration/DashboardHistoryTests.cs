using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupClient.Tests.Integration;

public partial class BackupServiceIntegrationTests
{
    [Theory]
    [InlineData("targetName")]
    [InlineData("relativePath")]
    public async Task Dashboard_MalformedHistoricalDiagnosticsRemainUnavailableWithoutLeakingValues(string missing)
    {
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        var options = new DashboardOptions { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "reporting") };
        await CreateReportingService(target, options.ReportingDirectory).RunAsync(default);
        var path = Path.Combine(options.ReportingDirectory, "history.json");
        var history = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var attempt = history["attempts"]![0]!;
        var entry = new JsonObject { ["at"] = attempt["startedAt"]!.GetValue<string>(), ["category"] = "skipped",
            ["targetName"] = "documents", ["relativePath"] = "file.txt", ["action"] = "Check file access and readability, then retry the backup." };
        entry[missing] = null;
        attempt["diagnostics"] = new JsonObject { ["skipped"] = 1, ["entries"] = new JsonArray(entry) };
        await File.WriteAllTextAsync(path, history.ToJsonString());
        await using var companion = await DashboardCompanion.StartAsync(options);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options);
        using var response = await browser.GetAsync("api/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", document.RootElement.GetProperty("state").GetString());
        Assert.Empty(document.RootElement.GetProperty("attempts").EnumerateArray());
    }

    [Fact]
    public async Task Dashboard_DiagnosticNamesAndReadErrorsAreSanitizedBeforePersistenceAndHttp()
    {
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        var names = new[] { "SYNTHETIC-SECRET-token.txt", "seed-phrase.txt", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzZWNyZXQifQ.signaturesignaturesignature" };
        foreach (var name in names) await File.WriteAllTextAsync(Path.Combine(target, name), "abc");
        var options = Options.Create(new BackupClientOptions { BackupTargets = new() { ["SYNTHETIC-SECRET-token"] = target },
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "reporting") } });
        using var state = OpenState(Path.Combine(_testRoot, "state.db"));
        var real = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(x => x.ScanDirectoryStreamAsync(It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .Returns((string path, string[]? exclusions, CancellationToken token) => real.ScanDirectoryStreamAsync(path, exclusions, token));
        fileSystem.Setup(x => x.ComputeFileHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("SYNTHETIC-SECRET Bearer test recovery material https://storage.test?sig=SAS-SECRET"));
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state,
            new BackupScanner(fileSystem.Object, state, NullLogger<BackupScanner>.Instance), _testUploader, options);
        Assert.Equal(BackupOutcome.CompletedWithErrors, (await service.RunAsync(default)).Outcome);
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        var views = new[] { await browser.GetStringAsync("api/history"), await browser.GetStringAsync("api/status") }
            .Concat(await Task.WhenAll(Directory.GetFiles(options.Value.Dashboard.ReportingDirectory, "*.json").Select(path => File.ReadAllTextAsync(path))));
        foreach (var view in views)
        {
            Assert.DoesNotContain("SYNTHETIC-SECRET", view);
            Assert.DoesNotContain("seed-phrase", view);
            Assert.DoesNotContain("eyJhbGci", view);
            Assert.DoesNotContain("SAS-SECRET", view);
            Assert.DoesNotContain(target, view);
        }
        using var history = JsonDocument.Parse(await browser.GetStringAsync("api/history"));
        var diagnostics = history.RootElement.GetProperty("attempts")[0].GetProperty("diagnostics");
        Assert.Equal(3, diagnostics.GetProperty("skipped").GetInt32());
        Assert.All(diagnostics.GetProperty("entries").EnumerateArray(), entry => {
            Assert.Equal("[redacted target]", entry.GetProperty("targetName").GetString());
            Assert.Equal("[redacted path]", entry.GetProperty("relativePath").GetString());
        });
    }

    [Fact]
    public async Task Dashboard_PruningRetainsPendingOriginalEvidenceAndEarlierDegradedObservationsForLaterReconciliation()
    {
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "original");
        var options = Options.Create(new BackupClientOptions { CommitStatusTimeoutSeconds = 0,
            BackupTargets = new() { ["documents"] = target }, Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "reporting") } });
        using var state = OpenState(Path.Combine(_testRoot, "state.db"));
        var actualFileSystem = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(x => x.ScanDirectoryStreamAsync(It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .Returns((string path, string[]? exclusions, CancellationToken token) => actualFileSystem.ScanDirectoryStreamAsync(path, exclusions, token));
        fileSystem.Setup(x => x.ComputeFileHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken token) => path.EndsWith("blocked.txt", StringComparison.Ordinal)
                ? Task.FromException<string>(new IOException("SYNTHETIC-SECRET Bearer SECRET https://storage.test?sig=SAS-SECRET"))
                : actualFileSystem.ComputeFileHashAsync(path, token));
        BackupService Service() => new(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state,
            new BackupScanner(fileSystem.Object, state, NullLogger<BackupScanner>.Instance), _testUploader, options);
        var baseline = await Service().RunAsync(default);
        Assert.Equal(BackupOutcome.Succeeded, baseline.Outcome);
        await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "new content");
        await File.WriteAllTextAsync(Path.Combine(target, "blocked.txt"), "unreadable");
        var complete = false;
        _mockApiClient.Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid device, Guid commit, CancellationToken _) => new CommitStatusResponse {
                DeviceId = device, RunId = _committedRuns[commit], CommitId = commit,
                Status = complete ? CommitJobStatus.Succeeded : CommitJobStatus.Processing });
        var origin = await Service().RunAsync(default);
        Assert.Equal(BackupOutcome.Pending, origin.Outcome);
        var originalRun = Assert.Single(origin.Runs);
        for (var i = 0; i < 103; i++) Assert.Equal(BackupOutcome.Pending, (await Service().RunAsync(default)).Outcome);
        var pending = (await state.GetPendingBackupRunAsync(origin.DeviceId!.Value))!;
        Assert.Equal(origin.AttemptId, pending.Evidence!.OriginAttemptId);
        Assert.Equal(originalRun.ScanStartedAt, pending.Evidence.ScanStartedAt);
        Assert.Equal(1, pending.Evidence.FilesSkipped);
        Assert.Equal(100, pending.Evidence.AttemptIds.Length);
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        using (var before = JsonDocument.Parse(await browser.GetStringAsync("api/history")))
        {
            var attempts = before.RootElement.GetProperty("attempts");
            Assert.Equal(100, attempts.GetArrayLength());
            Assert.DoesNotContain(attempts.EnumerateArray(), a => a.GetProperty("attemptId").GetGuid() == origin.AttemptId);
            Assert.DoesNotContain(attempts.EnumerateArray(), a => a.GetProperty("attemptId").GetGuid() == baseline.AttemptId);
            Assert.All(attempts.EnumerateArray(), a => Assert.Equal("degraded", a.GetProperty("runs")[0].GetProperty("cleanliness").GetString()));
        }
        using (var status = JsonDocument.Parse(await browser.GetStringAsync("api/status")))
        {
            var clean = status.RootElement.GetProperty("snapshot").GetProperty("evidence").GetProperty("lastCleanResult");
            Assert.Equal(baseline.AttemptId, clean.GetProperty("attemptId").GetGuid());
            Assert.Equal(Assert.Single(baseline.Runs).ScanStartedAt, clean.GetProperty("scanStartedAt").GetDateTimeOffset());
        }
        Assert.True(File.Exists(Path.Combine(options.Value.Dashboard.ReportingDirectory, "clean-evidence.json")));
        complete = true;
        var reconciled = await Service().RunAsync(default);
        Assert.Equal(BackupOutcome.CompletedWithErrors, reconciled.Outcome);
        Assert.Null(await state.GetPendingBackupRunAsync(origin.DeviceId.Value));
        using var after = JsonDocument.Parse(await browser.GetStringAsync("api/history"));
        var retained = after.RootElement.GetProperty("attempts");
        Assert.Equal("completed", retained[0].GetProperty("runs")[0].GetProperty("localCompletion").GetString());
        Assert.Equal(origin.AttemptId, retained[0].GetProperty("runs")[0].GetProperty("originAttemptId").GetGuid());
        Assert.Equal("pending", retained[1].GetProperty("runs")[0].GetProperty("localCompletion").GetString());
        Assert.Equal("degraded", retained[1].GetProperty("runs")[0].GetProperty("cleanliness").GetString());
        using (var status = JsonDocument.Parse(await browser.GetStringAsync("api/status")))
        {
            var clean = status.RootElement.GetProperty("snapshot").GetProperty("evidence").GetProperty("lastCleanResult");
            Assert.Equal(baseline.AttemptId, clean.GetProperty("attemptId").GetGuid());
            Assert.Equal(Assert.Single(baseline.Runs).ScanStartedAt, clean.GetProperty("scanStartedAt").GetDateTimeOffset());
        }
        foreach (var path in Directory.GetFiles(options.Value.Dashboard.ReportingDirectory, "*.json"))
        {
            var text = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("SYNTHETIC-SECRET", text);
            Assert.DoesNotContain("sas-token", text);
            Assert.DoesNotContain(target, text);
        }
    }

    [Fact]
    public async Task Dashboard_HistoryExplainsFilesChangedAfterHashing()
    {
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "changing.txt"), "abc");
        var options = Options.Create(new BackupClientOptions { BackupTargets = new() { ["documents"] = target },
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "reporting") } });
        var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "state.db") }), NullLogger<BackupStateService>.Instance);
        var real = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        var scanner = new Mock<IBackupScanner>();
        scanner.Setup(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyDictionary<string, string> targets, string[]? excludes, CancellationToken token) => real.ScanAllTargetsAsync(targets, excludes, token));
        scanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .Returns(async (TaggedFile file, CancellationToken token) => {
                var result = await real.AnalyzeFileAsync(file, token);
                await File.AppendAllTextAsync(file.Metadata.FilePath, "changed after hashing", token);
                return result;
            });
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner.Object, _testUploader, options);
        Assert.Equal(BackupOutcome.CompletedWithErrors, (await service.RunAsync(default)).Outcome);
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        using var history = JsonDocument.Parse(await browser.GetStringAsync("api/history"));
        var diagnostics = history.RootElement.GetProperty("attempts")[0].GetProperty("diagnostics");
        Assert.Equal(1, diagnostics.GetProperty("changed").GetInt32());
        Assert.Equal("changing.txt", diagnostics.GetProperty("entries")[0].GetProperty("relativePath").GetString());
        Assert.Contains("stops changing", diagnostics.GetProperty("entries")[0].GetProperty("action").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_HistoryReportsOnlyValidatedServerRejectedFilesWithoutRemoteErrorText(bool wrongIdentity)
    {
        using var state = OpenState(Path.Combine(_testRoot, "state.db"));
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid(); var commit = Guid.NewGuid(); _committedRuns[commit] = run;
        await StageRunAsync(state, device, run, commit);
        ConfigurePartialStatus();
        var page = FailedPage(device, wrongIdentity ? Guid.NewGuid() : run, commit);
        page = page with { Files = page.Files.Select(file => file with { Error = "SYNTHETIC-SECRET Bearer test https://storage.test?sig=SAS-SECRET" }).ToArray() };
        _mockApiClient.Setup(x => x.ListFailedCommitFiles(device, commit, 500, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(page);
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        var options = Options.Create(new BackupClientOptions { BackupTargets = new() { ["default"] = target },
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "reporting") } });
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state,
            new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance), _testUploader, options);
        if (wrongIdentity) await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(default));
        else Assert.Equal(BackupOutcome.CompletedWithErrors, (await service.RunAsync(default)).Outcome);
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        var historyText = await browser.GetStringAsync("api/history");
        Assert.DoesNotContain("SYNTHETIC-SECRET", historyText);
        Assert.DoesNotContain("SAS-SECRET", historyText);
        Assert.DoesNotContain("SYNTHETIC-SECRET", await File.ReadAllTextAsync(Path.Combine(options.Value.Dashboard.ReportingDirectory, "history.json")));
        using var history = JsonDocument.Parse(historyText);
        var diagnostics = history.RootElement.GetProperty("attempts")[0].GetProperty("diagnostics");
        Assert.Equal(1, diagnostics.GetProperty("serverRejected").GetInt32()); // Validated commit status supplies the total.
        if (!wrongIdentity) Assert.Equal("rejected.txt", diagnostics.GetProperty("entries")[0].GetProperty("relativePath").GetString());
        else
        {
            Assert.NotNull(await state.GetPendingBackupRunAsync(device));
            Assert.Empty(diagnostics.GetProperty("entries").EnumerateArray());
            Assert.True(diagnostics.GetProperty("truncated").GetBoolean());
        }
    }

    [Fact]
    public async Task Dashboard_HistoryShowsBoundedReadSkipSamplesWithCompleteCountsAndSafePaths()
    {
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        for (var i = 0; i < 120; i++) await File.WriteAllTextAsync(Path.Combine(target, $"file-{i:000}.txt"), "abc");
        await File.WriteAllTextAsync(Path.Combine(target, "SYNTHETIC-SECRET-token.txt"), "abc");
        var options = Options.Create(new BackupClientOptions { BackupTargets = new() { ["documents"] = target },
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "reporting") } });
        var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "state.db") }), NullLogger<BackupStateService>.Instance);
        var real = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        var scanner = new Mock<IBackupScanner>();
        scanner.Setup(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyDictionary<string, string> targets, string[]? excludes, CancellationToken token) => real.ScanAllTargetsAsync(targets, excludes, token));
        scanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile file, CancellationToken token) => (file, false, FileChangeType.Skipped));
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner.Object, _testUploader, options);
        Assert.Equal(BackupOutcome.CompletedWithErrors, (await service.RunAsync(default)).Outcome);
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        var json = await browser.GetStringAsync("api/history");
        using var document = JsonDocument.Parse(json);
        var attempt = document.RootElement.GetProperty("attempts")[0];
        var diagnostics = attempt.GetProperty("diagnostics");
        Assert.Equal(121, diagnostics.GetProperty("skipped").GetInt32());
        Assert.True(diagnostics.GetProperty("truncated").GetBoolean());
        Assert.Equal(100, diagnostics.GetProperty("entries").GetArrayLength());
        Assert.All(diagnostics.GetProperty("entries").EnumerateArray(), entry => {
            Assert.Equal("documents", entry.GetProperty("targetName").GetString());
            Assert.Equal("skipped", entry.GetProperty("category").GetString());
            Assert.Contains("retry", entry.GetProperty("action").GetString());
            Assert.True(entry.GetProperty("at").GetDateTimeOffset() >= attempt.GetProperty("startedAt").GetDateTimeOffset());
        });
        Assert.DoesNotContain(target, json);
        foreach (var path in Directory.GetFiles(options.Value.Dashboard.ReportingDirectory, "*.json"))
        {
            var stored = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("SYNTHETIC-SECRET", stored);
            Assert.DoesNotContain("sas-token", stored);
            Assert.DoesNotContain(target, stored);
        }
    }

    [Fact]
    public async Task Dashboard_HistoryRetainsLatestHundredAttemptsAcrossClientAndCompanionRestarts()
    {
        var target = Path.Combine(_testRoot, "target"); Directory.CreateDirectory(target);
        var options = new DashboardOptions { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "reporting") };
        var ids = new List<Guid>();
        for (var i = 0; i < 105; i++)
            ids.Add((await CreateReportingService(target, options.ReportingDirectory).RunAsync(default)).AttemptId);
        for (var restart = 0; restart < 2; restart++)
        {
            await using var companion = await DashboardCompanion.StartAsync(options);
            using var browser = new HttpClient { BaseAddress = companion.Address };
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/history")).StatusCode);
            await DashboardTestAuthorization.AuthorizeAsync(browser, options);
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync("api/history?path=ignored")).StatusCode);
            using var history = JsonDocument.Parse(await browser.GetStringAsync("api/history"));
            var attempts = history.RootElement.GetProperty("attempts");
            Assert.Equal(100, attempts.GetArrayLength());
            Assert.Equal(ids[104], attempts[0].GetProperty("attemptId").GetGuid());
            Assert.Equal(ids[5], attempts[99].GetProperty("attemptId").GetGuid());
            Assert.All(attempts.EnumerateArray(), a => Assert.Equal("cleanCheck", a.GetProperty("result").GetString()));
        }
    }
}
