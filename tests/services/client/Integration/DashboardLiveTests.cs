using System.Text.Json;
using FlorisDeV.BackupClient.Clients.BackupApi;
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
    public async Task Dashboard_QuietApiWaitHeartbeatsReconnectsAndBecomesStaleWithoutDeclaringExit()
    {
        var target = Path.Combine(_testRoot, "live-target"); Directory.CreateDirectory(target);
        var options = Options.Create(new BackupClientOptions { BackupTargets = new() { ["documents"] = target },
            Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "live-reporting") } });
        var clock = new LiveClock();
        Guid registeredDevice = default;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DeviceRegistrationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockApiClient.Setup(x => x.RegisterDevice(It.IsAny<RegisterDeviceRequest>(), It.IsAny<CancellationToken>()))
            .Returns((RegisterDeviceRequest request, CancellationToken token) => { registeredDevice = request.DeviceId!.Value; entered.TrySetResult(); return release.Task; });
        var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "live-state.db") }), NullLogger<BackupStateService>.Instance);
        var scanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner, _testUploader, options, clock);
        var attempt = service.RunAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard, clock);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        var first = await ReadLiveAsync(browser);
        Assert.Equal("active", first.GetProperty("result").GetString());
        Assert.True(first.GetProperty("live").GetProperty("apiWaking").GetBoolean());
        var id = first.GetProperty("attemptId").GetGuid();
        clock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(async () => (await ReadLiveAsync(browser)).GetProperty("live").GetProperty("heartbeatAt").GetDateTimeOffset() == clock.GetUtcNow());
        using var reloaded = new HttpClient { BaseAddress = companion.Address };
        reloaded.DefaultRequestHeaders.Authorization = browser.DefaultRequestHeaders.Authorization;
        Assert.Equal(id, (await ReadLiveAsync(reloaded)).GetProperty("attemptId").GetGuid());
        clock.AdvanceWithoutTimers(TimeSpan.FromSeconds(16));
        var stale = JsonDocument.Parse(await browser.GetStringAsync("api/status")).RootElement;
        Assert.True(stale.GetProperty("stale").GetBoolean());
        Assert.Equal("active", stale.GetProperty("snapshot").GetProperty("result").GetString());
        release.SetResult(new DeviceRegistrationResponse { DeviceId = registeredDevice, TenantId = "test-tenant", UserId = "test-user" });
        Assert.Equal(BackupOutcome.NoChanges, (await attempt).Outcome);
        var terminal = await ReadLiveAsync(browser);
        Assert.False(terminal.GetProperty("live").GetProperty("active").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("cleanCheck", (await ReadLiveAsync(browser)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Dashboard_OverlappingScanUploadCoalescesCountsAndCountsOnlySuccessfulTransferBytes()
    {
        var target = Path.Combine(_testRoot, "batch-target"); Directory.CreateDirectory(target);
        for (var i = 0; i < 103; i++) await File.WriteAllTextAsync(Path.Combine(target, $"{i:000}.txt"), "abc");
        var options = Options.Create(new BackupClientOptions { MaxFailurePercentage = 100,
            BackupTargets = new() { ["documents"] = target }, Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_testRoot, "batch-reporting") } });
        var clock = new LiveClock();
        var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = Path.Combine(_testRoot, "batch-state.db") }), NullLogger<BackupStateService>.Instance);
        var actualScanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance);
        var scanner = new Mock<IBackupScanner>();
        scanner.Setup(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyDictionary<string, string> targets, string[]? excludes, CancellationToken token) => actualScanner.ScanAllTargetsAsync(targets, excludes, token));
        var analyzed = 0;
        var hashEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hashRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .Returns(async (TaggedFile file, CancellationToken token) => {
                var number = ++analyzed;
                if (number == 103) { hashEntered.SetResult(); await hashRelease.Task; }
                if (number == 101) return (file, false, FileChangeType.Unchanged);
                if (number == 102) return (file, false, FileChangeType.Skipped);
                return await actualScanner.AnalyzeFileAsync(file, token);
            });
        var uploadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockApiClient.Setup(x => x.RefreshBackupRunSas(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefreshSasUrlResponse { SasUrlInfo = new FlorisDeV.BackupContracts.Infrastructure.SasUrlInfo {
                Url = new Uri("https://storage.blob.core.windows.net/backups?sig=SYNTHETIC-SAS"), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), TtlMinutes = 60 } });
        var uploads = 0;
        var uploader = new Mock<IFileUploader>();
        uploader.Setup(x => x.UploadFilesAsync(It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(), It.IsAny<IReadOnlyList<TaggedFile>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Azure.Storage.Blobs.BlobContainerClient client, IReadOnlyList<TaggedFile> files, CancellationToken token) => {
                if (++uploads == 1) {
                    uploadEntered.SetResult(); await uploadRelease.Task;
                    return new UploadBatchResult(files.Take(98).Select(f => f with { UploadSizeBytes = 5 }).ToArray(), [files[98]], 1) { FailedFiles = [files[99]] };
                }
                if (uploads == 2) return new UploadBatchResult([], files, 0);
                return new UploadBatchResult(files, [], 0);
            });
        uploader.Setup(x => x.UploadRunManifestAsync(It.IsAny<Azure.Storage.Blobs.BlobContainerClient>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IAsyncEnumerable<FlorisDeV.BackupContracts.Manifest.ManifestFileEntry>>(), It.IsAny<IAsyncEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((Azure.Storage.Blobs.BlobContainerClient client, Guid device, Guid run, IAsyncEnumerable<FlorisDeV.BackupContracts.Manifest.ManifestFileEntry> files, IAsyncEnumerable<string> deleted, string? path, bool embedded, CancellationToken token) => _testUploader.UploadRunManifestAsync(client, device, run, files, deleted, path, embedded, token));
        var service = new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object, state, scanner.Object, uploader.Object, options, clock);
        var attempt = service.RunAsync(default);
        await uploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var companion = await DashboardCompanion.StartAsync(options.Value.Dashboard, clock);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options.Value.Dashboard);
        var path = Path.Combine(options.Value.Dashboard.ReportingDirectory, "snapshot.json");
        var initial = await File.ReadAllTextAsync(path);
        clock.Advance(TimeSpan.FromMilliseconds(800));
        Assert.Equal(initial, await File.ReadAllTextAsync(path)); // 100 file observations have coalesced.
        clock.Advance(TimeSpan.FromMilliseconds(200));
        await EventuallyAsync(async () => (await ReadLiveAsync(browser)).GetProperty("live").GetProperty("uploading").GetBoolean());
        var overlapping = await ReadLiveAsync(browser);
        Assert.Equal(100, overlapping.GetProperty("filesScanned").GetInt32());
        var live = overlapping.GetProperty("live");
        Assert.True(live.GetProperty("scanning").GetBoolean());
        Assert.Equal(JsonValueKind.Null, live.GetProperty("totalFilesToScan").ValueKind);
        Assert.Equal(JsonValueKind.Null, live.GetProperty("uploadPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, live.GetProperty("estimatedSecondsRemaining").ValueKind);
        uploadRelease.SetResult();
        await hashEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(async () => (await ReadLiveAsync(browser)).GetProperty("filesScanned").GetInt32() == 103);
        var hashing = (await ReadLiveAsync(browser)).GetProperty("live");
        Assert.Equal(98, hashing.GetProperty("filesUploaded").GetInt32());
        Assert.Equal(490, hashing.GetProperty("uploadedBytes").GetInt64());
        Assert.Equal(1, hashing.GetProperty("filesUnchanged").GetInt32());
        Assert.Equal(1, hashing.GetProperty("filesSkipped").GetInt32());
        Assert.Equal(1, hashing.GetProperty("filesDeferred").GetInt32());
        Assert.Equal(1, hashing.GetProperty("filesFailed").GetInt32());
        Assert.Equal("documents", hashing.GetProperty("currentTarget").GetString());
        Assert.Equal(245, hashing.GetProperty("averageBytesPerSecond").GetDouble());
        await companion.DisposeAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        await using var restarted = await DashboardCompanion.StartAsync(options.Value.Dashboard, clock);
        using var reconnected = new HttpClient { BaseAddress = restarted.Address };
        await DashboardTestAuthorization.AuthorizeAsync(reconnected, options.Value.Dashboard);
        await EventuallyAsync(async () => (await ReadLiveAsync(reconnected)).GetProperty("live").GetProperty("heartbeatAt").GetDateTimeOffset() == clock.GetUtcNow());
        hashRelease.SetResult();
        Assert.Equal(BackupOutcome.CompletedWithErrors, (await attempt).Outcome);
        var terminal = (await ReadLiveAsync(reconnected)).GetProperty("live");
        Assert.Equal(99, terminal.GetProperty("filesUploaded").GetInt32());
        Assert.Equal(493, terminal.GetProperty("uploadedBytes").GetInt64());
        Assert.Equal(103, terminal.GetProperty("totalFilesToScan").GetInt32());
        Assert.Equal(3, terminal.GetProperty("warnings").GetArrayLength());
        using var history = JsonDocument.Parse(await reconnected.GetStringAsync("api/history"));
        var diagnostics = history.RootElement.GetProperty("attempts")[0].GetProperty("diagnostics");
        Assert.Equal(1, diagnostics.GetProperty("failed").GetInt32());
        Assert.Equal(1, diagnostics.GetProperty("deferred").GetInt32());
        Assert.Equal(1, diagnostics.GetProperty("skipped").GetInt32());
        Assert.False(diagnostics.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, diagnostics.GetProperty("entries").GetArrayLength());
        using var finalStatus = JsonDocument.Parse(await reconnected.GetStringAsync("api/status"));
        Assert.Equal("unknown", finalStatus.RootElement.GetProperty("freshness").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, finalStatus.RootElement.GetProperty("snapshot").GetProperty("evidence").GetProperty("lastCleanResult").ValueKind);
    }

    private static async Task<JsonElement> ReadLiveAsync(HttpClient browser)
    {
        using var json = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
        return json.RootElement.GetProperty("snapshot").Clone();
    }
    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++) { if (await condition()) return; await Task.Delay(10); }
        Assert.Fail("Expected live observation was not recorded.");
    }
    private sealed class LiveClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        private readonly List<LiveTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceWithoutTimers(TimeSpan by) => _now += by;
        public void Advance(TimeSpan by) { _now += by; foreach (var timer in _timers.ToArray()) timer.Tick(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new LiveTimer(callback, state, () => _now, dueTime, period); _timers.Add(timer); return timer; }
        private sealed class LiveTimer(TimerCallback callback, object? state, Func<DateTimeOffset> now, TimeSpan due, TimeSpan period) : ITimer
        {
            private bool _disposed;
            private DateTimeOffset _next = now() + due;
            public void Tick() { if (!_disposed && now() >= _next) { _next = now() + period; callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
