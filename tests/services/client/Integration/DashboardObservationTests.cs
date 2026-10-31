using System.Text.Json;
using System.Text.Json.Nodes;
using FlorisDeV.BackupClient.Dashboard;

namespace FlorisDeV.BackupClient.Tests.Integration;

public sealed partial class BackupExecutionProcessTests
{
    [Fact]
    public async Task ObservationsRecoverAfterCompanionRestartAndDistinguishSilenceFromConfirmedClientDeath()
    {
        Directory.CreateDirectory(_root);
        var options = ControlOptions();
        var owner = Start(Path.Combine(_root, "state.db"), "owner");
        await WaitForFile("owner.entered");
        // Controlled observation time makes the latest heartbeat stale while execution stays alive.
        var time = new ObservationClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var runner = new ControlledSystemd(_ => throw new InvalidOperationException("Duplicate activation forbidden"));
        var companion = await DashboardCompanion.StartAsync(options.Dashboard, time, startControl: Control(options, runner));
        try
        {
            using var browser = await AuthorizedBrowser(companion, options.Dashboard);
            var stale = await Status(browser);
            Assert.Equal("stale", stale.GetProperty("observation").GetString());
            Assert.Equal("active", stale.GetProperty("executionState").GetString());
            var attempt = stale.GetProperty("snapshot").GetProperty("attemptId").GetGuid();
            Assert.Contains("alreadyRunning", await (await browser.PostAsync("api/start", null)).Content.ReadAsStringAsync());
            await companion.DisposeAsync();
            companion = await DashboardCompanion.StartAsync(options.Dashboard, time, startControl: Control(options, runner));
            using var reconnected = await AuthorizedBrowser(companion, options.Dashboard);
            Assert.Equal(attempt, (await Status(reconnected)).GetProperty("snapshot").GetProperty("attemptId").GetGuid());
            owner.Kill(entireProcessTree: true); await owner.WaitForExitAsync();
            var interrupted = await Status(reconnected);
            Assert.Equal("interrupted", interrupted.GetProperty("observation").GetString());
            Assert.Equal("inactive", interrupted.GetProperty("executionState").GetString());
            Assert.Equal(attempt, interrupted.GetProperty("snapshot").GetProperty("attemptId").GetGuid());
            var next = Start(Path.Combine(_root, "state.db"), "next");
            await WaitForFile("next.entered");
            await File.WriteAllTextAsync(Path.Combine(_root, "next.release"), "release");
            await next.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var recorded = await Status(reconnected);
            Assert.Equal("recorded", recorded.GetProperty("observation").GetString());
            Assert.Equal("cleanCheck", recorded.GetProperty("snapshot").GetProperty("result").GetString());
        }
        finally { await companion.DisposeAsync(); }
    }

    [Fact]
    public async Task ObserverDoesNotCreateOrInitializeMissingBackupState()
    {
        Directory.CreateDirectory(_root);
        var options = ControlOptions();
        var missing = Path.Combine(_root, "missing-parent", "never-created.db");
        var control = new DashboardStartControl(new() { FilePath = missing }, options);
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, startControl: control);
        using var browser = await AuthorizedBrowser(companion, options.Dashboard);
        var status = await Status(browser);
        Assert.Equal("empty", status.GetProperty("state").GetString());
        Assert.Equal("inactive", status.GetProperty("executionState").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReplacementExecutionCannotMakeADeadAttemptAppearAliveWhenReportingFails(bool reusedPid)
    {
        Directory.CreateDirectory(_root);
        var options = ControlOptions();
        var database = Path.Combine(_root, "state.db");
        var old = Start(database, "old"); await WaitForFile("old.entered");
        var snapshot = await File.ReadAllTextAsync(Path.Combine(options.Dashboard.ReportingDirectory, "snapshot.json"));
        old.Kill(entireProcessTree: true); await old.WaitForExitAsync();
        if (reusedPid)
        {
            // Simulate the recorded PID being reused by a living process with a different birth identity.
            var receiptPath = Path.Combine(options.Dashboard.ReportingDirectory, "execution.json");
            var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
            receipt["processId"] = Environment.ProcessId;
            receipt["processIdentity"] = receipt["processIdentity"]!.GetValue<string>().Split(':')[0] + ":0";
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        }
        var replacement = Start(database, "replacement", mode: "unreported");
        await WaitForFile("replacement.entered");
        var runner = new ControlledSystemd(_ => throw new InvalidOperationException("Duplicate activation forbidden"));
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, startControl: Control(options, runner));
        using var browser = await AuthorizedBrowser(companion, options.Dashboard);
        var status = await Status(browser);
        Assert.Equal("interrupted", status.GetProperty("observation").GetString());
        Assert.Equal("active", status.GetProperty("executionState").GetString());
        Assert.Contains("alreadyRunning", await (await browser.PostAsync("api/start", null)).Content.ReadAsStringAsync());
        Assert.Equal(snapshot, await File.ReadAllTextAsync(Path.Combine(options.Dashboard.ReportingDirectory, "snapshot.json")));
    }

    [Fact]
    public async Task MissingExecutionIdentityLeavesAttemptUnknownEvenWhenSomeProcessOwnsExecution()
    {
        Directory.CreateDirectory(_root);
        var options = ControlOptions();
        Start(Path.Combine(_root, "state.db"), "owner"); await WaitForFile("owner.entered");
        var receiptPath = Path.Combine(options.Dashboard.ReportingDirectory, "execution.json");
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
        receipt.AsObject().Remove("processId"); receipt.AsObject().Remove("processIdentity");
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, startControl: Control(options, new ControlledSystemd(_ => Task.CompletedTask)));
        using var browser = await AuthorizedBrowser(companion, options.Dashboard);
        var status = await Status(browser);
        Assert.Equal("unavailable", status.GetProperty("observation").GetString());
        Assert.Equal("active", status.GetProperty("executionState").GetString());
        Assert.Contains("alreadyRunning", await (await browser.PostAsync("api/start", null)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MalformedReportingPreservesOnlyPreviouslyVerifiedEvidenceAndNeverReturnsOldSuccessAsCurrent()
    {
        Directory.CreateDirectory(_root);
        var options = ControlOptions();
        var process = Start(Path.Combine(_root, "state.db"), "done");
        await WaitForFile("done.entered");
        await File.WriteAllTextAsync(Path.Combine(_root, "done.release"), "release");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var companion = await DashboardCompanion.StartAsync(options.Dashboard, startControl: Control(options, new ControlledSystemd(_ => Task.CompletedTask)));
        using var browser = await AuthorizedBrowser(companion, options.Dashboard);
        var verified = await Status(browser);
        Assert.Equal("recorded", verified.GetProperty("observation").GetString());
        var attempt = verified.GetProperty("snapshot").GetProperty("attemptId").GetGuid();
        var path = Path.Combine(options.Dashboard.ReportingDirectory, "snapshot.json");
        var corrupted = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        corrupted["targetNames"] = new JsonArray { null };
        await File.WriteAllTextAsync(path, corrupted.ToJsonString());
        var invalid = await Status(browser);
        Assert.Equal("unavailable", invalid.GetProperty("state").GetString());
        Assert.Equal("unavailable", invalid.GetProperty("observation").GetString());
        Assert.Equal(attempt, invalid.GetProperty("snapshot").GetProperty("attemptId").GetGuid());
        Assert.DoesNotContain("fresh", invalid.GetRawText());
    }

    private static async Task<JsonElement> Status(HttpClient browser)
    {
        using var doc = JsonDocument.Parse(await browser.GetStringAsync("api/status"));
        return doc.RootElement.Clone();
    }
    private sealed class ObservationClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
