using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient.Tests.ProcessFixture;

namespace FlorisDeV.BackupClient.Tests.Integration;

public sealed class DashboardSchedulingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stowhaven-scheduling-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = [];

    [Fact]
    public async Task AuthorizedBrowserShowsActualTimerIndependentlyOfMissingBackupReporting()
    {
        var backup = Options();
        var systemd = new SchedulingSystemd();
        await using var companion = await DashboardCompanion.StartAsync(backup.Dashboard,
            startControl: new(new() { FilePath = Path.Combine(_root, "unused.db") }, backup, systemd));
        using var browser = new HttpClient { BaseAddress = companion.Address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/status")).StatusCode);
        await DashboardTestAuthorization.AuthorizeAsync(browser, backup.Dashboard);
        var data = await browser.GetFromJsonAsync<JsonElement>("api/status");
        var scheduling = data.GetProperty("scheduling");
        Assert.Equal("enabled", scheduling.GetProperty("state").GetString());
        Assert.True(scheduling.GetProperty("enabled").GetBoolean());
        Assert.Equal(DateTimeOffset.Parse("2031-11-02T01:30:00Z"), scheduling.GetProperty("nextActivation").GetDateTimeOffset());
        Assert.Equal("empty", data.GetProperty("state").GetString());
        Assert.False(File.Exists(Path.Combine(_root, "unused.db")));
        Assert.Contains("Scheduling", await browser.GetStringAsync("/"));
    }

    [Fact]
    public async Task TimerCannotPromiseRepeatedAttemptsWhenOneshotServiceRemainsActiveAfterExit()
    {
        var browser = await SubprocessBrowser("LoadState=loaded\nActiveState=active\nType=oneshot\nRemainAfterExit=yes\n",
            "LoadState=loaded\nActiveState=active\nUnitFileState=enabled\nUnit=stowhaven-synthetic.service\nNextElapseUSecRealtime=@1951359000\n");
        using (browser)
        {
            var data = await browser.GetFromJsonAsync<JsonElement>("api/status");
            Assert.Equal("incompatibleSchedule", data.GetProperty("scheduling").GetProperty("state").GetString());
        }
        Assert.False(File.Exists(Path.Combine(_root, "unused.db")));
    }

    [Theory]
    [InlineData("disabled", "disabled", false)]
    [InlineData("enabled-runtime", "enabled", true)]
    [InlineData("static", "unknown", null)]
    [InlineData("transient", "unknown", null)]
    [InlineData("masked", "masked", false)]
    public async Task BrowserSeparatesEnablementFromAnUnknownNextActivation(string unitFileState, string state, bool? enabled)
    {
        var systemd = new SchedulingSystemd { Timer = new("loaded", "inactive", unitFileState, null, "backup-client.service") };
        var data = await Status(systemd);
        var schedule = data.GetProperty("scheduling");
        Assert.Equal(state, schedule.GetProperty("state").GetString());
        Assert.Equal(enabled, schedule.GetProperty("enabled").ValueKind == JsonValueKind.Null ? null : schedule.GetProperty("enabled").GetBoolean());
        Assert.Equal("unknown", schedule.GetProperty("nextActivationState").GetString());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("nextActivation").ValueKind);
        Assert.Contains("No next time is inferred", schedule.GetProperty("message").GetString());
        Assert.Equal("empty", data.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("service")]
    [InlineData("timer")]
    public async Task BrowserExplainsMissingUserUnitsWithoutCallingThemDisabled(string missing)
    {
        var systemd = new SchedulingSystemd();
        if (missing == "service") systemd.Service = new("not-found", "inactive", "unknown");
        else systemd.Timer = new("not-found", "inactive", "unknown", null);
        var schedule = (await Status(systemd)).GetProperty("scheduling");
        Assert.Equal("missingUnit", schedule.GetProperty("state").GetString());
        Assert.Contains("Install", schedule.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("enabled").ValueKind);
    }

    [Theory]
    [InlineData("resident")]
    [InlineData("simple")]
    [InlineData("differentService")]
    public async Task BrowserExplainsUnsupportedSchedulingWithoutChangingIt(string configuration)
    {
        var systemd = new SchedulingSystemd();
        if (configuration == "simple") systemd.Service = new("loaded", "active", "simple");
        if (configuration == "differentService") systemd.Timer = systemd.Timer with { ServiceUnit = "another.service" };
        var backup = Options(resident: configuration == "resident");
        var schedule = (await Status(systemd, backup)).GetProperty("scheduling");
        Assert.Equal("incompatibleSchedule", schedule.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("nextActivation").ValueKind);
        Assert.Contains(configuration == "differentService" ? "Unit=" : configuration == "resident" ? "Schedule:Enabled" : "Type=oneshot", schedule.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("schedulerUnavailable")]
    [InlineData("userSessionUnavailable")]
    [InlineData("permissionDenied")]
    public async Task UnavailableScheduleHasActionableGuidanceAndNeverLooksDisabled(string category)
    {
        var schedule = (await Status(new SchedulingSystemd { Error = category })).GetProperty("scheduling");
        Assert.Equal(category, schedule.GetProperty("state").GetString());
        Assert.Contains("Check", schedule.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("enabled").ValueKind);
    }

    [Theory]
    [InlineData("@1700000000.123456", "2023-11-14T22:13:20.123456+00:00")]
    [InlineData("n/a", null)]
    [InlineData("", null)]
    [InlineData("@0", null)]
    [InlineData("18446744073709551615", null)]
    [InlineData("Sun 2031-11-02 02:30:00 CET", null)]
    public async Task SubprocessTimerDatesAreMachineReadableOrHonestlyUnknown(string value, string? expected)
    {
        using var browser = await SubprocessBrowser("LoadState=loaded\nActiveState=inactive\nType=oneshot\nRemainAfterExit=no\n",
            "LoadState=loaded\nActiveState=active\nUnitFileState=enabled\nUnit=stowhaven-synthetic.service\nNextElapseUSecRealtime=" + value + "\n");
        var schedule = (await browser.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("scheduling");
        Assert.Equal("enabled", schedule.GetProperty("state").GetString());
        Assert.Equal(expected == null ? "unknown" : "known", schedule.GetProperty("nextActivationState").GetString());
        if (expected != null) Assert.Equal(DateTimeOffset.Parse(expected), schedule.GetProperty("nextActivation").GetDateTimeOffset());
        else Assert.Equal(JsonValueKind.Null, schedule.GetProperty("nextActivation").ValueKind);
        Assert.False(File.Exists(Path.Combine(_root, "unused.db")));
    }

    [Fact]
    public async Task ActualReadOnlySystemctlWithNonexistentUserBusReportsSessionUnavailableThroughAuthorizedHttp()
    {
        using var browser = await SubprocessBrowser("", "", unavailableBus: true);
        var schedule = (await browser.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("scheduling");
        Assert.Equal("userSessionUnavailable", schedule.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("enabled").ValueKind);
        Assert.Contains("user", schedule.GetProperty("message").GetString());
        Assert.False(File.Exists(Path.Combine(_root, "unused.db")));
    }

    [Fact]
    public async Task MaskedTimerFromActualCommandShapeHasActionableMaskedStatus()
    {
        using var browser = await SubprocessBrowser("LoadState=loaded\nActiveState=inactive\nType=oneshot\n",
            "LoadState=masked\nActiveState=inactive\nUnitFileState=masked\n");
        var schedule = (await browser.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("scheduling");
        Assert.Equal("masked", schedule.GetProperty("state").GetString());
        Assert.False(schedule.GetProperty("enabled").GetBoolean());
        Assert.Contains("unmask", schedule.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ConfigurationReloadDetectsResidentSchedulingWithoutRestartingTheCompanion()
    {
        var current = Options();
        await using var companion = await DashboardCompanion.StartAsync(current.Dashboard,
            startControl: new(new() { FilePath = Path.Combine(_root, "unused.db") }, current, new SchedulingSystemd()), currentOptions: () => current);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, current.Dashboard);
        Assert.Equal("enabled", (await browser.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("scheduling").GetProperty("state").GetString());
        current = Options(resident: true);
        var schedule = (await browser.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("scheduling");
        Assert.Equal("incompatibleSchedule", schedule.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("nextActivation").ValueKind);
    }

    [Fact]
    public async Task ConfiguredUnitsSelectAnIsolatedInstallationAndFollowConfigurationReload()
    {
        var current = Options();
        var systemd = new SchedulingSystemd();
        await using var companion = await DashboardCompanion.StartAsync(current.Dashboard,
            startControl: new(new() { FilePath = Path.Combine(_root, "unused.db") }, current, systemd), currentOptions: () => current);
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, current.Dashboard);
        Assert.Equal("enabled", (await browser.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("scheduling").GetProperty("state").GetString());
        current = new() { BackupTargets = new(), Dashboard = new()
        {
            ReportingDirectory = current.Dashboard.ReportingDirectory,
            BackupServiceUnit = "stowhaven-smoke-unique.service", BackupTimerUnit = "stowhaven-smoke-unique.timer"
        } };
        systemd.ExpectedService = "stowhaven-smoke-unique.service";
        systemd.ExpectedTimer = "stowhaven-smoke-unique.timer";
        systemd.Timer = systemd.Timer with { ServiceUnit = "stowhaven-smoke-unique.service", UnitFileState = "disabled", NextActivation = null };
        var schedule = (await browser.GetFromJsonAsync<JsonElement>("api/status")).GetProperty("scheduling");
        Assert.Equal("disabled", schedule.GetProperty("state").GetString());
        Assert.False(schedule.GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("../other.timer")]
    [InlineData("*.timer")]
    [InlineData("backup-client.service")]
    [InlineData("stowhaven-synthetic.timer;touch forbidden")]
    public async Task ConfiguredTimerMustBeAnExactUnitWithoutPathsOrShellSyntax(string unit)
    {
        using var browser = await SubprocessBrowser("LoadState=loaded\nActiveState=inactive\nType=oneshot\n", "", timerUnit: unit);
        var text = await browser.GetStringAsync("api/status");
        var schedule = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("scheduling");
        Assert.Equal("invalidUnit", schedule.GetProperty("state").GetString());
        Assert.DoesNotContain(unit, text);
        Assert.False(File.Exists(Path.Combine(_root, "unused.db")));
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_root, "commands.log"))); // Only the valid service read reached the boundary.
    }

    [Fact]
    public async Task OversizedSchedulerOutputIsUnavailableWithoutForwardingRawData()
    {
        var secret = "synthetic-secret-not-for-dashboard";
        using var browser = await SubprocessBrowser("LoadState=loaded\nActiveState=inactive\nType=oneshot\n", new string('x', 17000) + secret);
        var text = await browser.GetStringAsync("api/status");
        var schedule = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("scheduling");
        Assert.Equal("schedulerUnavailable", schedule.GetProperty("state").GetString());
        Assert.DoesNotContain(secret, text);
        Assert.Contains("too much", schedule.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnresponsiveSchedulerCommandIsBoundedAndDoesNotExposeRawErrors()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "timer.delay"), "delay");
        using var browser = await SubprocessBrowser("LoadState=loaded\nActiveState=inactive\nType=oneshot\n", "");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var schedule = (await browser.GetFromJsonAsync<JsonElement>("api/status", deadline.Token)).GetProperty("scheduling");
        Assert.Equal("userSessionUnavailable", schedule.GetProperty("state").GetString());
        Assert.Contains("did not respond", schedule.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SubprocessReadinessIsInvisibleUntilItsCompleteAddressCanBeConsumed()
    {
        var startup = SubprocessBrowser("LoadState=loaded\nActiveState=inactive\nType=oneshot\n", "", pauseReadiness: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(Path.Combine(_root, "schedule.writing"))) await Task.Delay(10, timeout.Token);
        try { Assert.False(File.Exists(Path.Combine(_root, "schedule.ready")), "A partially written address must not be published as ready."); }
        finally { await File.WriteAllTextAsync(Path.Combine(_root, "schedule.publish"), "publish"); }
        using var browser = await startup;
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("api/status")).StatusCode);
    }

    private async Task<JsonElement> Status(SchedulingSystemd systemd, BackupClientOptions? backup = null)
    {
        backup ??= Options();
        await using var companion = await DashboardCompanion.StartAsync(backup.Dashboard,
            startControl: new(new() { FilePath = Path.Combine(_root, "unused.db") }, backup, systemd));
        using var browser = new HttpClient { BaseAddress = companion.Address };
        await DashboardTestAuthorization.AuthorizeAsync(browser, backup.Dashboard);
        return await browser.GetFromJsonAsync<JsonElement>("api/status");
    }

    private async Task<HttpClient> SubprocessBrowser(string serviceOutput, string timerOutput, bool unavailableBus = false, string? timerUnit = null, bool pauseReadiness = false)
    {
        Directory.CreateDirectory(_root);
        if (!unavailableBus)
        {
            var commands = Path.Combine(_root, "commands"); Directory.CreateDirectory(commands);
            var executable = Path.Combine(commands, "systemctl");
            await File.WriteAllTextAsync(Path.Combine(_root, "service.output"), serviceOutput);
            await File.WriteAllTextAsync(Path.Combine(_root, "timer.output"), timerOutput);
            await File.WriteAllTextAsync(executable, """
                #!/usr/bin/python3
                import pathlib, sys, time
                root = pathlib.Path(__file__).resolve().parent.parent
                args = sys.argv[1:]
                with (root / 'commands.log').open('a') as log: log.write(repr(args) + '\n')
                if len(args) < 4 or args[:2] != ['--user', 'show'] or args[-2] != '--':
                    print('Unexpected mutation command', file=sys.stderr)
                    sys.exit(1)
                unit = args[-1]
                if unit not in ['stowhaven-synthetic.service', 'stowhaven-synthetic.timer']:
                    sys.exit(1)
                if unit.endswith('.timer') and '--timestamp=unix' not in args:
                    print('Machine readable timestamp required', file=sys.stderr)
                    sys.exit(1)
                if unit.endswith('.timer') and (root / 'timer.delay').exists(): time.sleep(30)
                print((root / ('timer.output' if unit.endswith('.timer') else 'service.output')).read_text(), end='')
                """);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "FlorisDeV.BackupClient.Tests.runtimeconfig.json"), "--depsfile", Path.Combine(AppContext.BaseDirectory, "FlorisDeV.BackupClient.Tests.deps.json"), typeof(FixtureProgram).Assembly.Location, _root, Path.Combine(_root, "unused.db"), "schedule", "scheduleHttp" })
            start.ArgumentList.Add(argument);
        if (timerUnit != null || pauseReadiness) start.ArgumentList.Add(timerUnit ?? "stowhaven-synthetic.timer");
        if (pauseReadiness) start.ArgumentList.Add("pauseReady");
        if (!unavailableBus) start.Environment["PATH"] = Path.Combine(_root, "commands") + Path.PathSeparator + start.Environment["PATH"];
        // This also prevents accidentally reading the actual manager if a fixture command is absent.
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=" + Path.Combine(_root, "nonexistent-bus");
        start.Environment["XDG_RUNTIME_DIR"] = _root;
        var child = Process.Start(start)!; _children.Add(child);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(Path.Combine(_root, "schedule.ready")))
        {
            if (child.HasExited) throw new InvalidOperationException(await child.StandardError.ReadToEndAsync());
            await Task.Delay(20, timeout.Token);
        }
        var browser = new HttpClient { BaseAddress = new Uri(await File.ReadAllTextAsync(Path.Combine(_root, "schedule.ready"))) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/status")).StatusCode);
        using (var hostile = new HttpRequestMessage(HttpMethod.Get, "api/status"))
        {
            hostile.Headers.Add("Origin", "http://hostile.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(hostile)).StatusCode);
        }
        Assert.False(File.Exists(Path.Combine(_root, "commands.log")));
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options().Dashboard);
        return browser;
    }

    private BackupClientOptions Options(bool resident = false) => new()
    {
        BackupTargets = new(), Dashboard = new() { ReportingDirectory = Path.Combine(_root, "reporting") }, Schedule = new() { Enabled = resident }
    };
    private sealed class SchedulingSystemd : ISystemdUserService
    {
        public SystemdServiceState Service { get; set; } = new("loaded", "inactive", "oneshot");
        public SystemdTimerState Timer { get; set; } = new("loaded", "active", "enabled", DateTimeOffset.Parse("2031-11-02T01:30:00Z"), "backup-client.service");
        public string? Error { get; init; }
        public string ExpectedService { get; set; } = "backup-client.service";
        public string ExpectedTimer { get; set; } = "backup-client.timer";
        public Task<SystemdServiceState> ShowAsync(string unit, CancellationToken token)
        { Assert.Equal(ExpectedService, unit); return Task.FromResult(Service); }
        public Task<SystemdTimerState> ShowTimerAsync(string unit, CancellationToken token)
        { Assert.Equal(ExpectedTimer, unit); return Error != null ? throw new SystemdControlException(Error, "Check the synthetic user units and session.") : Task.FromResult(Timer); }
        public Task StartAsync(string unit, CancellationToken token)
            => throw new InvalidOperationException("Reading scheduler state must never start a backup.");
    }
    public void Dispose()
    {
        foreach (var child in _children) { if (!child.HasExited) child.Kill(entireProcessTree: true); child.WaitForExit(); child.Dispose(); }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
