using System.Text.Json;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FlorisDeV.BackupClient.Tests.Integration;

public partial class BackupServiceIntegrationTests
{
    [Fact]
    public async Task PendingRun_ExitsInactiveAndReconcilesInAnotherAttemptBeforeCheckingCurrentFiles()
    {
        var target = Path.Combine(_testRoot, "pending-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "synthetic");
        var options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new() { ["documents"] = target }, CommitStatusTimeoutSeconds = 0,
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "pending-reporting") }
        });
        var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "pending.db") }), NullLogger<BackupStateService>.Instance);
        var scanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        BackupService Service() => new(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner, _testUploader, options);
        var complete = false;
        _mockApiClient.Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid device, Guid commit, CancellationToken _) => new CommitStatusResponse
            {
                DeviceId = device, RunId = _committedRuns[commit], CommitId = commit,
                Status = complete ? CommitJobStatus.Succeeded : CommitJobStatus.Processing
            });
        var pending = await Service().RunAsync(default);
        Assert.Equal(BackupOutcome.Pending, pending.Outcome);
        var run = Assert.Single(pending.Runs);
        Assert.Equal(BackupServerProcessing.Pending, run.ServerProcessing);
        Assert.Equal(BackupLocalCompletion.Pending, run.LocalCompletion);
        Assert.Equal(pending.AttemptId, run.OriginAttemptId);
        var durable = await state.GetPendingBackupRunAsync(pending.DeviceId!.Value);
        Assert.Equal(run.ScanStartedAt, durable!.Evidence!.ScanStartedAt);
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        using (var json = JsonDocument.Parse(await browser.GetStringAsync("api/status")))
        {
            var snapshot = json.RootElement.GetProperty("snapshot");
            Assert.False(snapshot.GetProperty("clientActive").GetBoolean());
            Assert.Equal("pending", snapshot.GetProperty("runs")[0].GetProperty("serverProcessing").GetString());
        }
        var intermediate = await Service().RunAsync(default);
        Assert.Equal(BackupOutcome.Pending, intermediate.Outcome);
        var intermediateHeader = (await state.GetPendingBackupRunAsync(pending.DeviceId.Value))!;
        Assert.Contains(intermediate.AttemptId, intermediateHeader.Evidence!.AttemptIds);
        // A commit response lost before the header update is retried idempotently.
        await state.SavePendingBackupRunAsync(intermediateHeader with { CommitId = null });
        var stillPending = await Service().RunAsync(default);
        Assert.Equal(BackupOutcome.Pending, stillPending.Outcome);
        var retriedHeader = (await state.GetPendingBackupRunAsync(pending.DeviceId.Value))!;
        Assert.Contains(stillPending.AttemptId, retriedHeader.Evidence!.AttemptIds);
        complete = true;
        var resumed = await Service().RunAsync(default);
        Assert.Equal(BackupOutcome.Succeeded, resumed.Outcome);
        Assert.NotEqual(pending.AttemptId, resumed.AttemptId);
        var finished = Assert.Single(resumed.Runs);
        Assert.Equal(run.RunId, finished.RunId);
        Assert.Equal(run.ScanStartedAt, finished.ScanStartedAt);
        Assert.Equal(BackupLocalCompletion.Completed, finished.LocalCompletion);
        Assert.Equal(BackupCleanliness.Clean, finished.Cleanliness);
        Assert.Equal(new[] { pending.AttemptId, intermediate.AttemptId, stillPending.AttemptId, resumed.AttemptId }, finished.AttemptIds);
        Assert.Equal(1, resumed.FilesScanned);
        Assert.Null(await state.GetPendingBackupRunAsync(pending.DeviceId.Value));
        Assert.Equal(run.RunId, (await state.GetOrCreateDeviceStateAsync()).LastRunId);
    }
    [Fact]
    public async Task ServerFinishedRun_WithInvalidRejectedFileEvidence_RemainsLocallyPendingInFailedAttempt()
    {
        using var state = OpenState(Path.Combine(_testRoot, "blocked.db"));
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        _committedRuns[commit] = run;
        await StageRunAsync(state, device, run, commit);
        ConfigurePartialStatus();
        _mockApiClient.Setup(x => x.ListFailedCommitFiles(device, commit, 500, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedPage(device, Guid.NewGuid(), commit));
        var source = Path.Combine(_testRoot, "source"); Directory.CreateDirectory(source);
        var reporting = Path.Combine(_testRoot, "blocked-reporting");
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object,
            state, new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance),
            _testUploader, Options.Create(new BackupClientOptions { BackupTargets = new() { ["default"] = source },
                Dashboard = new() { Enabled = true, ReportingDirectory = reporting } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(default));
        await using var companion = await DashboardCompanion.StartAsync(new() { ReportingDirectory = reporting });
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, new() { ReportingDirectory = reporting });
        using var json = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
        var snapshot = json.RootElement.GetProperty("snapshot");
        Assert.Equal("failed", snapshot.GetProperty("result").GetString());
        var evidence = snapshot.GetProperty("runs")[0];
        Assert.Equal("completedWithErrors", evidence.GetProperty("serverProcessing").GetString());
        Assert.Equal("pending", evidence.GetProperty("localCompletion").GetString());
        Assert.Equal("pending", evidence.GetProperty("outcome").GetString());
        var completed = snapshot.GetProperty("evidence").GetProperty("lastCompletedServerBackup");
        Assert.Equal("recorded", snapshot.GetProperty("serverBackupEvidence").GetString());
        Assert.Equal(run, completed.GetProperty("runId").GetGuid());
        Assert.Equal("completedWithErrors", completed.GetProperty("outcome").GetString());
        Assert.Equal("pending", completed.GetProperty("localCompletion").GetString());
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("serverCompletedAt").ValueKind);
        Assert.NotEqual(default, completed.GetProperty("serverCompletionObservedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("evidence").GetProperty("lastCleanResult").ValueKind);
        Assert.Equal("unknown", json.RootElement.GetProperty("freshness").GetProperty("state").GetString());
        Assert.NotNull(await state.GetPendingBackupRunAsync(device));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task PendingEvidence_PreservesOriginalScanAndUncertaintyAcrossEveryHeaderUpdateAndNewRun(bool legacy, bool newRun)
    {
        using var state = OpenState(Path.Combine(_testRoot, "evidence.db"));
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid(); var origin = Guid.NewGuid();
        var originalScan = DateTimeOffset.UtcNow.AddDays(-2);
        await StageRunAsync(state, device, run, null);
        var header = (await state.GetPendingBackupRunAsync(device))!;
        await state.SavePendingBackupRunAsync(header with
        {
            ManifestUploaded = false,
            Evidence = legacy ? null : new BackupRunResult
            {
                RunId = run, OriginAttemptId = origin, AttemptIds = [origin], ScanStartedAt = originalScan,
                SelectionFingerprint = new string('a', 64), FilesSkipped = 2, FilesDeferred = 3, FilesFailed = 4,
                ScanCompleted = true, Cleanliness = BackupCleanliness.Degraded
            }
        });
        await CreateSourceFileAsync("first.txt", "first");
        var complete = false;
        _mockApiClient.Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid d, Guid c, CancellationToken _) => new CommitStatusResponse
            { DeviceId = d, RunId = _committedRuns[c], CommitId = c, Status = complete ? CommitJobStatus.Succeeded : CommitJobStatus.Processing });
        var pending = await CreateServiceWithState(state).RunAsync(default);
        Assert.Equal(BackupOutcome.Pending, pending.Outcome);
        var saved = (await state.GetPendingBackupRunAsync(device))!.Evidence!;
        Assert.Equal(legacy ? null : originalScan, saved.ScanStartedAt);
        Assert.Equal(legacy ? null : new string('a', 64), saved.SelectionFingerprint);
        Assert.Equal(legacy ? null : 2, saved.FilesSkipped);
        Assert.Equal(legacy ? null : 3, saved.FilesDeferred);
        Assert.Equal(legacy ? null : 4, saved.FilesFailed);
        Assert.Equal(legacy ? BackupCleanliness.Unknown : BackupCleanliness.Degraded, saved.Cleanliness);
        Assert.Equal(legacy ? null : true, saved.ScanCompleted);
        Assert.True(pending.CurrentScan!.Clean);
        if (newRun) await CreateSourceFileAsync("new.txt", "new clean work");
        complete = true;
        var finished = await CreateServiceWithState(state).RunAsync(default);
        Assert.Equal(legacy ? BackupOutcome.Succeeded : BackupOutcome.CompletedWithErrors, finished.Outcome);
        Assert.Equal(newRun ? 2 : 1, finished.Runs.Length);
        Assert.True(finished.CurrentScan!.Clean);
        Assert.Equal(!newRun, finished.CurrentScan.NoChanges);
        var old = finished.Runs.Single(x => x.RunId == run);
        Assert.Equal(legacy ? BackupCleanliness.Unknown : BackupCleanliness.Degraded, old.Cleanliness);
        Assert.Equal(legacy ? null : originalScan, old.ScanStartedAt);
        Assert.Equal(BackupLocalCompletion.Completed, old.LocalCompletion);
        Assert.Contains(pending.AttemptId, old.AttemptIds);
        Assert.Contains(finished.AttemptId, old.AttemptIds);
        if (newRun)
        {
        var clean = finished.Runs.Single(x => x.RunId != run);
        Assert.Equal(BackupCleanliness.Clean, clean.Cleanliness);
        Assert.Equal(BackupRunOutcome.Succeeded, clean.Outcome);
        Assert.Equal(finished.AttemptId, clean.OriginAttemptId);
        Assert.Equal(0, clean.FilesFailed);
        Assert.Equal(clean.RunId, (await state.GetOrCreateDeviceStateAsync()).LastRunId);
        }
        Assert.Null(await state.GetPendingBackupRunAsync(device));
    }

    [Fact]
    public async Task AbortedAttempt_PreservesPendingRunAndEarlierDegradation()
    {
        using var state = OpenState(Path.Combine(_testRoot, "aborted.db"));
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid(); var origin = Guid.NewGuid();
        await StageRunAsync(state, device, run, null);
        var header = (await state.GetPendingBackupRunAsync(device))!;
        await state.SavePendingBackupRunAsync(header with { ManifestUploaded = false, Evidence = new BackupRunResult
        {
            RunId = run, OriginAttemptId = origin, AttemptIds = [origin], ScanStartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            SelectionFingerprint = new string('b', 64), FilesSkipped = 2, FilesDeferred = 0, FilesFailed = 0,
            ScanCompleted = false, Cleanliness = BackupCleanliness.Degraded
        }});
        await CreateSourceFileAsync("one.txt", "one");
        await CreateSourceFileAsync("two.txt", "two");
        var realScanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        var scanner = new Mock<IBackupScanner>();
        scanner.Setup(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyDictionary<string, string> targets, string[]? excludes, CancellationToken token) => realScanner.ScanAllTargetsAsync(targets, excludes, token));
        var analyzed = 0;
        scanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .Returns((TaggedFile file, CancellationToken _) => ++analyzed == 1
                ? Task.FromResult((file, false, FileChangeType.Skipped))
                : Task.FromException<(TaggedFile, bool, FileChangeType)>(new OperationCanceledException("synthetic abort")));
        var options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new() { ["default"] = Path.Combine(_testRoot, "source") },
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "aborted-reporting") }
        });
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object,
            state, scanner.Object, _testUploader, options);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunAsync(default));
        var saved = (await state.GetPendingBackupRunAsync(device))!;
        Assert.Equal(3, saved.Evidence!.FilesSkipped);
        Assert.Equal(header.RunId, saved.RunId);
        Assert.Equal(new string('b', 64), saved.Evidence.SelectionFingerprint);
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        using var json = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
        var snapshot = json.RootElement.GetProperty("snapshot");
        Assert.Equal("failed", snapshot.GetProperty("result").GetString());
        Assert.Equal("pending", snapshot.GetProperty("runs")[0].GetProperty("outcome").GetString());
        Assert.Equal(3, snapshot.GetProperty("runs")[0].GetProperty("filesSkipped").GetInt32());
        Assert.False(snapshot.GetProperty("clientActive").GetBoolean());
    }

    [Theory]
    [InlineData(BackupOutcome.NoChanges, "cleanCheck")]
    [InlineData(BackupOutcome.Succeeded, "succeeded")]
    [InlineData(BackupOutcome.CompletedWithErrors, "completedWithErrors")]
    [InlineData(BackupOutcome.Pending, "pending")]
    [InlineData(BackupOutcome.Failed, "failed")]
    public async Task Dashboard_RecordsAllFiveDistinctAttemptOutcomes(BackupOutcome outcome, string displayed)
    {
        var target = Path.Combine(_testRoot, "outcome-target"); Directory.CreateDirectory(target);
        if (outcome != BackupOutcome.NoChanges) await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "synthetic");
        var state = OpenState(Path.Combine(_testRoot, "outcome.db"));
        var realScanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        IBackupScanner scanner = realScanner;
        if (outcome == BackupOutcome.CompletedWithErrors)
        {
            var skipped = new Mock<IBackupScanner>();
            skipped.Setup(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyDictionary<string, string> targets, string[]? excludes, CancellationToken token) => realScanner.ScanAllTargetsAsync(targets, excludes, token));
            skipped.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TaggedFile file, CancellationToken _) => (file, false, FileChangeType.Skipped));
            scanner = skipped.Object;
        }
        if (outcome == BackupOutcome.Pending)
            _mockApiClient.Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid d, Guid c, CancellationToken _) => new CommitStatusResponse
                { DeviceId = d, RunId = _committedRuns[c], CommitId = c, Status = CommitJobStatus.Processing });
        if (outcome == BackupOutcome.Failed)
            _mockApiClient.Setup(x => x.RegisterDevice(It.IsAny<FlorisDeV.BackupContracts.Api.Requests.RegisterDeviceRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("synthetic failure"));
        var options = Options.Create(new BackupClientOptions { BackupTargets = new() { ["documents"] = target },
            CommitStatusTimeoutSeconds = 0, Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "outcome-reporting") } });
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner, _testUploader, options);
        if (outcome == BackupOutcome.Failed) await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(default));
        else Assert.Equal(outcome, (await service.RunAsync(default)).Outcome);
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        using var json = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
        Assert.Equal(displayed, json.RootElement.GetProperty("snapshot").GetProperty("result").GetString());
    }

    [Fact]
    public async Task Dashboard_ReportsPendingRunWhileAwaitingServerCompletion()
    {
        var target = Path.Combine(_testRoot, "waiting-target"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "synthetic");
        using var state = OpenState(Path.Combine(_testRoot, "waiting.db"));
        var options = Options.Create(new BackupClientOptions { BackupTargets = new() { ["documents"] = target },
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "waiting-reporting") } });
        var clock = new LiveClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockApiClient.Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid device, Guid commit, CancellationToken _) =>
            {
                entered.TrySetResult(); await release.Task;
                return new CommitStatusResponse { DeviceId = device, RunId = _committedRuns[commit], CommitId = commit, Status = CommitJobStatus.Succeeded };
            });
        var scanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner, _testUploader, options, clock);
        var attempt = service.RunAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard, clock);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        JsonElement waiting;
        try
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await EventuallyAsync(async () => (await ReadLiveAsync(browser)).GetProperty("live").GetProperty("heartbeatAt").GetDateTimeOffset() == clock.GetUtcNow());
            waiting = await ReadLiveAsync(browser);
        }
        finally { release.TrySetResult(); await attempt; }
        Assert.True(waiting.GetProperty("clientActive").GetBoolean());
        var run = Assert.Single(waiting.GetProperty("runs").EnumerateArray());
        Assert.Equal("pending", run.GetProperty("serverProcessing").GetString());
        Assert.Equal("pending", run.GetProperty("localCompletion").GetString());
        Assert.NotEqual(Guid.Empty, run.GetProperty("commitId").GetGuid());
    }

    [Fact]
    public async Task ExpiredUnsubmittedRun_RetainsItsFailedResultWhenReplacementRunSucceeds()
    {
        using var state = OpenState(Path.Combine(_testRoot, "expired.db"));
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid(); var origin = Guid.NewGuid();
        await StageRunAsync(state, device, run, null);
        var header = (await state.GetPendingBackupRunAsync(device))!;
        await state.SavePendingBackupRunAsync(header with
        {
            ManifestUploaded = false,
            UploadSasUrlInfo = new() { Url = header.UploadSasUrlInfo.Url, TtlMinutes = 60, ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) },
            ManifestSasUrlInfo = new() { Url = header.ManifestSasUrlInfo.Url, TtlMinutes = 60, ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) },
            Evidence = new BackupRunResult { RunId = run, OriginAttemptId = origin, AttemptIds = [origin],
                ScanStartedAt = DateTimeOffset.UtcNow.AddDays(-1), SelectionFingerprint = new string('c', 64),
                ScanCompleted = false, FilesSkipped = 1, FilesDeferred = 0, FilesFailed = 0, Cleanliness = BackupCleanliness.Degraded }
        });
        await CreateSourceFileAsync("replacement.txt", "synthetic");
        var result = await CreateServiceWithState(state).RunAsync(default);
        Assert.Equal(BackupOutcome.CompletedWithErrors, result.Outcome);
        var expired = result.Runs.Single(x => x.RunId == run);
        Assert.Equal(BackupRunOutcome.Failed, expired.Outcome);
        Assert.Equal(BackupLocalCompletion.NotCompleted, expired.LocalCompletion);
        Assert.Equal(BackupCleanliness.Degraded, expired.Cleanliness);
        Assert.Equal(BackupCleanliness.Clean, result.Runs.Single(x => x.RunId != run).Cleanliness);
        Assert.Null(await state.GetPendingBackupRunAsync(device));
    }

}
