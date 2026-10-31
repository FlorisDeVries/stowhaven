using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlorisDeV.BackupClient.Dashboard;

namespace FlorisDeV.BackupClient.Tests.Integration;

public sealed class DashboardLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stowhaven-dashboard-logs", Guid.NewGuid().ToString("N"));
    private DashboardOptions Options => new()
    {
        Enabled = true,
        ReportingDirectory = Path.Combine(_root, "reporting"),
        LogDirectory = Path.Combine(_root, "logs")
    };
    private static HttpClient Browser(DashboardCompanion companion) => new(new HttpClientHandler { UseProxy = false }) { BaseAddress = companion.Address };
    private static string Entry(string message) => "2026-10-31 12:00:00.000 +00:00 [INF] " + message + "\n";
    private async Task WriteLogAsync(string content, string name = "backup-client-20261031.log")
    {
        Directory.CreateDirectory(Options.LogDirectory);
        await File.WriteAllTextAsync(Path.Combine(Options.LogDirectory, name), content);
    }

    [Fact]
    public async Task AuthorizedOperatorCanOpenUsefulRecentLogsWithoutServingCredentials()
    {
        await WriteLogAsync(Entry("Upload retry for documents/report.txt after timeout") + Entry("Authorization: Bearer SYNTHETIC-BEARER-SECRET"));
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        using var response = await browser.GetAsync("api/logs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        var logs = JsonDocument.Parse(body).RootElement;
        Assert.Equal("available", logs.GetProperty("state").GetString());
        Assert.Contains("documents/report.txt after timeout", body);
        Assert.Contains("redacted", body);
        Assert.DoesNotContain("SYNTHETIC-BEARER-SECRET", body);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("Open recent logs", await browser.GetStringAsync("/"));
        Assert.Contains("/api/logs", await browser.GetStringAsync("dashboard.js"));
    }

    [Fact]
    public async Task RecentViewBoundsEntriesAndCharactersAndOmitsIncompleteEvents()
    {
        var content = string.Concat(Enumerable.Range(0, 600).Select(i => Entry($"Retry {i:D3} for documents/report.txt")));
        await WriteLogAsync(content + Entry("Final complete observation") + "2026-10-31 12:00:00.000 +00:00 [INF] unfinished");
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        var logs = await browser.GetFromJsonAsync<JsonElement>("api/logs");
        var entries = logs.GetProperty("entries").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.InRange(entries.Length, 1, 200);
        Assert.InRange(entries.Sum(e => e.Length), 1, 32768);
        Assert.True(logs.GetProperty("truncated").GetBoolean());
        Assert.Contains(entries, e => e.Contains("Final complete observation"));
        Assert.DoesNotContain(entries, e => e.Contains("unfinished") || e.Contains("Retry 000"));
    }

    [Fact]
    public async Task ExistingDailyRotationRemainsUntouchedAndOnlyTwoNewestClientSourcesAreRead()
    {
        for (var day = 18; day <= 31; day++)
            await WriteLogAsync(Entry($"Observation for day {day}"), $"backup-client-202610{day}.log");
        await WriteLogAsync(Entry("UNRELATED-FILE-CONTENT"), "unrelated.log");
        await WriteLogAsync(Entry("UNRELATED-LOOKALIKE-CONTENT"), "backup-client-not-a-date.log");
        await WriteLogAsync(Entry("UNRELATED-INVALID-DATE-CONTENT"), "backup-client-99999999.log");
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        var body = await browser.GetStringAsync("api/logs");
        Assert.Contains("Observation for day 31", body);
        Assert.Contains("Observation for day 30", body);
        Assert.DoesNotContain("Observation for day 29", body);
        Assert.DoesNotContain("UNRELATED", body);
        File.Move(Path.Combine(Options.LogDirectory, "backup-client-20261031.log"), Path.Combine(Options.LogDirectory, "rotated-away"));
        body = await browser.GetStringAsync("api/logs");
        Assert.Contains("Observation for day 30", body);
        Assert.DoesNotContain("Observation for day 31", body);
        await WriteLogAsync(Entry("New active log after rotation"));
        Assert.Contains("New active log after rotation", await browser.GetStringAsync("api/logs"));
        Assert.Equal(14, Directory.GetFiles(Options.LogDirectory, "backup-client-202610*.log").Length);
        Assert.Equal(Entry("Observation for day 18"), await File.ReadAllTextAsync(Path.Combine(Options.LogDirectory, "backup-client-20261018.log")));
    }

    [Fact]
    public async Task SecretVariantsAndMultilineValuesNeverEscapeThroughReadOrTailBoundaries()
    {
        var secrets = new[]
        {
            "BEARER fixture-alpha-value",
            "https://storage.invalid/file?sv=2025&sig=fixture-beta-value",
            "SAS fragment: sv=2025&sp=w&sig=fixture-gamma-value",
            "{\"RecoveryPhrase\":\"fixture delta echo foxtrot golf hotel\"}",
            "recovery_phrase =\n  fixture-india-value\n  fixture-juliet-value",
            "{\"client_secret\":\"fixture-kilo-value\"}",
            "ClientSecret=\n fixture-lima-value",
            "{\"apiKey\":\"fixture-mike-value\"}",
            "{\"Sas\":\"fixture-november-value\"}",
            "{\"sig\":\"fixture-oscar-value\"}",
            "{\"Pwd\":\"fixture-papa-value\"}",
            "Basic c3ludGhldGljOnBhc3N3b3Jk",
            "sig%3Dfixture-quebec-value%26sv%3D2025",
            "eyJhbGciOiJub25lIn0.eyJzdWIiOiJzeW50aGV0aWMifQ.c3ludGhldGljLXNpZ25hdHVyZQ",
            "Bearer " + new string('x', 4090) + "fixture-romeo-value" + new string('y', 4090)
        };
        await WriteLogAsync(string.Concat(secrets.Select(Entry)) + Entry("Safe diagnostic after redaction"));
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        var body = await browser.GetStringAsync("api/logs");
        Assert.Contains("Safe diagnostic after redaction", body);
        Assert.DoesNotContain("fixture-", body);
        Assert.DoesNotContain("fixture delta", body);
        Assert.DoesNotContain("eyJhbGci", body);
        Assert.DoesNotContain("c3ludGhldGljOnBhc3N3b3Jk", body);
        // The key is outside the bounded tail; its unlabelled continuation must be discarded.
        await WriteLogAsync(Entry("recovery phrase: " + new string('z', 70000) + "\nsynthetic-tail-secret\n") + Entry("Safe newest diagnostic"));
        body = await browser.GetStringAsync("api/logs");
        Assert.Contains("Safe newest diagnostic", body);
        Assert.DoesNotContain("synthetic-tail-secret", body);
        Assert.DoesNotContain(new string('z', 20), body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("truncated").GetBoolean());
        // A cut can land inside a value that happens to resemble a log header.
        // The first cut line cannot be trusted as a complete event.
        var newest = Entry("Safe newest diagnostic");
        var cutLine = Entry("SYNTHETIC-CUTOFF-VALUE");
        var tail = cutLine + new string(' ', 32768 - cutLine.Length - newest.Length - 1) + "\n" + newest;
        await WriteLogAsync(Entry("RecoveryPhrase: " + new string('z', 70000)) + tail);
        body = await browser.GetStringAsync("api/logs");
        Assert.Contains("Safe newest diagnostic", body);
        Assert.DoesNotContain("SYNTHETIC-CUTOFF-VALUE", body);
        Assert.DoesNotContain(Directory.GetFiles(Options.ReportingDirectory), p => Path.GetFileName(p).Contains("log", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BrowserSelectorsAndSymlinkedSourcesCannotReadUnrelatedLocalFiles()
    {
        await WriteLogAsync(Entry("Expected client context"));
        var outside = Path.Combine(_root, "unrelated");
        Directory.CreateDirectory(outside);
        var unrelated = Path.Combine(outside, "backup-client-20261031.log");
        await File.WriteAllTextAsync(unrelated, Entry("UNRELATED-LOCAL-FILE"));
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        foreach (var selector in new[] { "?path=" + Uri.EscapeDataString(unrelated), "?file=../../unrelated", "?source=unrelated", "?limit=999999999" })
        {
            using var response = await browser.GetAsync("api/logs" + selector);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("UNRELATED-LOCAL-FILE", await response.Content.ReadAsStringAsync());
        }
        var logPath = Path.Combine(Options.LogDirectory, "backup-client-20261031.log");
        File.Delete(logPath);
        File.CreateSymbolicLink(logPath, unrelated);
        Assert.Equal("unavailable", (await browser.GetFromJsonAsync<JsonElement>("api/logs")).GetProperty("state").GetString());
        File.Delete(logPath);
        Directory.Delete(Options.LogDirectory);
        Directory.CreateSymbolicLink(Options.LogDirectory, outside);
        var body = await browser.GetStringAsync("api/logs");
        Assert.Equal("unavailable", JsonDocument.Parse(body).RootElement.GetProperty("state").GetString());
        Assert.DoesNotContain("UNRELATED-LOCAL-FILE", body);
        Assert.DoesNotContain(_root, body);
    }

    [Fact]
    public async Task MissingEmptyAndUnreadableLogsHaveActionableStatesAndRemainUntouched()
    {
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        var logs = await browser.GetFromJsonAsync<JsonElement>("api/logs");
        Assert.Equal("missing", logs.GetProperty("state").GetString());
        Assert.Contains("Run a backup attempt", logs.GetProperty("message").GetString());
        Assert.False(Directory.Exists(Options.LogDirectory));
        await WriteLogAsync("");
        logs = await browser.GetFromJsonAsync<JsonElement>("api/logs");
        Assert.Equal("empty", logs.GetProperty("state").GetString());
        Assert.Contains("retry", logs.GetProperty("message").GetString());
        var path = Path.Combine(Options.LogDirectory, "backup-client-20261031.log");
        await WriteLogAsync(Entry("private diagnostic"));
        var originalMode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            logs = await browser.GetFromJsonAsync<JsonElement>("api/logs");
            Assert.Equal("unavailable", logs.GetProperty("state").GetString());
            Assert.Contains("permissions", logs.GetProperty("message").GetString());
            Assert.Empty(logs.GetProperty("entries").EnumerateArray());
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(path));
        }
        finally { File.SetUnixFileMode(path, originalMode); }
        Assert.Equal(Entry("private diagnostic"), await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LogsRequireBearerAuthorizationAndRejectHostileOriginsHostsAndExpiredSessions()
    {
        await WriteLogAsync(Entry("Authorized diagnostic"));
        var clock = new DashboardLogClock();
        await using var companion = await DashboardCompanion.StartAsync(Options, clock);
        using var browser = Browser(companion);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/logs")).StatusCode);
        browser.DefaultRequestHeaders.Add("Cookie", "stowhaven-session=synthetic-cookie");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/logs")).StatusCode);
        browser.DefaultRequestHeaders.Remove("Cookie");
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        browser.DefaultRequestHeaders.Add("Origin", "https://unrelated.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("api/logs")).StatusCode);
        browser.DefaultRequestHeaders.Remove("Origin");
        browser.DefaultRequestHeaders.Host = "unrelated.invalid";
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("api/logs")).StatusCode);
        browser.DefaultRequestHeaders.Host = null;
        browser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("api/logs")).StatusCode);
        browser.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        clock.Advance(TimeSpan.FromHours(11));
        Assert.Contains("Authorized diagnostic", await browser.GetStringAsync("api/logs"));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/logs")).StatusCode);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        Assert.Contains("Authorized diagnostic", await browser.GetStringAsync("api/logs"));
        Assert.Equal(Entry("Authorized diagnostic"), await File.ReadAllTextAsync(Path.Combine(Options.LogDirectory, "backup-client-20261031.log")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class DashboardLogClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
