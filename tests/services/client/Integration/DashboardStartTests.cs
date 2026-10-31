using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Dashboard;

namespace FlorisDeV.BackupClient.Tests.Integration;

public sealed partial class BackupExecutionProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedStartConfirmsRealExecutionIncludingFastCompletedAttempts(bool finishBeforeStartReturns)
    {
        Directory.CreateDirectory(_root);
        var invocation = Guid.NewGuid().ToString("N");
        var options = ControlOptions();
        var runner = new ControlledSystemd(async token =>
        {
            if (finishBeforeStartReturns) await File.WriteAllTextAsync(Path.Combine(_root, "service.release"), "release", token);
            var process = Start(Path.Combine(_root, "state.db"), "service", invocation);
            await WaitForFile("service.entered");
            if (finishBeforeStartReturns) await process.WaitForExitAsync(token);
        }) { State = new("loaded", "inactive", "oneshot", invocation) };
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, startControl: Control(options, runner));
        using var browser = await AuthorizedBrowser(companion, options.Dashboard);
        var response = await browser.PostAsync("api/start", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DashboardStartResult>();
        Assert.Equal("started", result!.State);
        Assert.NotNull(result.AttemptId);
        Assert.NotNull(result.StartedAt);
        if (finishBeforeStartReturns) Assert.Contains("finished", result.Message);
        else
        {
            var duplicate = await (await browser.PostAsync("api/start", null)).Content.ReadFromJsonAsync<DashboardStartResult>();
            Assert.Equal("alreadyRunning", duplicate!.State);
            Assert.Equal(result.AttemptId, duplicate.AttemptId);
        }
    }

    [Fact]
    public async Task AcceptedServiceRequestWithoutClientExecutionIsAnActionableUnconfirmedError()
    {
        Directory.CreateDirectory(_root);
        var options = ControlOptions();
        var runner = new ControlledSystemd(_ => Task.CompletedTask);
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, startControl: Control(options, runner));
        using var browser = await AuthorizedBrowser(companion, options.Dashboard);
        var response = await browser.PostAsync("api/start", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DashboardStartResult>();
        Assert.Equal("unconfirmed", result!.State);
        Assert.Contains("journal", result.Message);
        Assert.Null(result.AttemptId);
    }

    [Theory]
    [InlineData("missingUnit")]
    [InlineData("userSessionUnavailable")]
    [InlineData("permissionDenied")]
    [InlineData("activationFailed")]
    public async Task SystemdFailuresReachAuthorizedBrowserWithActionableGuidance(string category)
    {
        Directory.CreateDirectory(_root);
        var options = ControlOptions();
        var runner = new ControlledSystemd(_ => throw new SystemdControlException(category, "Check the synthetic user service configuration and session."));
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, startControl: Control(options, runner));
        using var browser = await AuthorizedBrowser(companion, options.Dashboard);
        var response = await browser.PostAsync("api/start", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DashboardStartResult>();
        Assert.Equal(category, result!.State);
        Assert.Contains("Check", result.Message);
        Assert.False(File.Exists(Path.Combine(_root, "service.entered")));
    }

    [Fact]
    public async Task CompetingBrowserRequestsThroughAliasedDatabaseReturnWithoutQueuingAfterFastExit()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "state.db");
        await File.WriteAllBytesAsync(database, []);
        var alias = Path.Combine(_root, "alias.db");
        using (var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln") { ArgumentList = { database, alias } })!)
        { await link.WaitForExitAsync(); Assert.Equal(0, link.ExitCode); }
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Guid.NewGuid().ToString("N");
        var options = ControlOptions();
        var runner = new ControlledSystemd(async token =>
        {
            requested.SetResult(); await proceed.Task.WaitAsync(token);
            await File.WriteAllTextAsync(Path.Combine(_root, "service.release"), "release", token);
            var process = Start(database, "service", invocation); await process.WaitForExitAsync(token);
        }) { State = new("loaded", "inactive", "oneshot", invocation) };
        var firstControl = Control(options, runner);
        var aliasControl = new DashboardStartControl(new() { FilePath = alias }, options, runner, TimeSpan.FromSeconds(2));
        // Different authenticated companions/process entry points share the same inode reservation.
        var first = firstControl.StartAsync(); await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => aliasControl.StartAsync()));
        Assert.All(duplicates, result => Assert.Equal("alreadyRunning", result.State));
        proceed.SetResult(); Assert.Equal("started", (await first).State);
        await Task.Delay(300);
        Assert.Single(_children);
    }

    [Fact]
    public async Task ExpiredBrowserAuthorizationDoesNotAffectActiveClientExecution()
    {
        Directory.CreateDirectory(_root);
        var options = ControlOptions();
        var process = Start(Path.Combine(_root, "state.db"), "owner"); await WaitForFile("owner.entered");
        var clock = new StartClock();
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, clock, startControl: Control(options, new ControlledSystemd(_ => Task.CompletedTask)));
        using var browser = await AuthorizedBrowser(companion, options.Dashboard);
        clock.Now += TimeSpan.FromHours(13);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("api/start", null)).StatusCode);
        Assert.False(process.HasExited);
        await File.WriteAllTextAsync(Path.Combine(_root, "owner.release"), "release");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task SeparateStartProcessesShareReservationAndNeverQueueDuplicateAfterOwnerExits()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "state.db");
        var first = Start(database, "first", mode: "request");
        await WaitForFile("first.requested");
        var alias = Path.Combine(_root, "state-alias.db"); File.CreateSymbolicLink(alias, database);
        var duplicate = Start(alias, "duplicate", mode: "request");
        await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("alreadyRunning", (await duplicate.StandardOutput.ReadToEndAsync()).Trim());
        Assert.False(File.Exists(Path.Combine(_root, "duplicate.requested")));
        await File.WriteAllTextAsync(Path.Combine(_root, "first.release"), "release");
        await File.WriteAllTextAsync(Path.Combine(_root, "first.launch"), "launch");
        await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("started", (await first.StandardOutput.ReadToEndAsync()).Trim());
        Assert.False(File.Exists(Path.Combine(_root, "duplicate.entered")));
        // The terminated competing process has no worker or queued execution left behind.
        var later = Start(database, "later"); await WaitForFile("later.entered");
        await File.WriteAllTextAsync(Path.Combine(_root, "later.release"), "release");
        await later.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, later.ExitCode);
    }

    [Fact]
    public async Task ActualSystemctlSubprocessWithUnavailableIsolatedBusProducesActionableSessionError()
    {
        Directory.CreateDirectory(_root);
        var probe = Start(Path.Combine(_root, "unused.db"), "probe", mode: "systemdProbe", unavailableUserBus: true);
        await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, probe.ExitCode);
        Assert.Equal("userSessionUnavailable", (await probe.StandardOutput.ReadToEndAsync()).Trim());
        Assert.False(File.Exists(Path.Combine(_root, "unused.db")));
    }

    private BackupClientOptions ControlOptions() => new() { BackupTargets = new(), Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(_root, "reporting") } };
    private DashboardStartControl Control(BackupClientOptions options, ISystemdUserService runner) => new(new() { FilePath = Path.Combine(_root, "state.db") }, options, runner, TimeSpan.FromMilliseconds(500));
    private static async Task<HttpClient> AuthorizedBrowser(DashboardCompanion companion, DashboardOptions options)
    {
        var browser = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, options); return browser;
    }
    private sealed class ControlledSystemd(Func<CancellationToken, Task> start) : ISystemdUserService
    {
        public SystemdServiceState State { get; set; } = new("loaded", "inactive", "oneshot", Guid.NewGuid().ToString("N"));
        public Task<SystemdServiceState> ShowAsync(string unit, CancellationToken token) => Task.FromResult(State);
        public Task StartAsync(string unit, CancellationToken token) => start(token);
    }
    private sealed class StartClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
