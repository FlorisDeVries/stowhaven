using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlorisDeV.BackupClient.Dashboard;

namespace FlorisDeV.BackupClient.Tests.Integration;

public sealed class DashboardHttpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stowhaven-dashboard-http", Guid.NewGuid().ToString("N"));
    private DashboardOptions Options => new() { Enabled = true, ReportingDirectory = _root };
    private static HttpClient Browser(DashboardCompanion companion) => new(new HttpClientHandler { UseProxy = false }) { BaseAddress = companion.Address };

    [Fact]
    public async Task BackupStartRequiresBrowserAuthorizationAndOriginProtection()
    {
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("api/start", null)).StatusCode);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        browser.DefaultRequestHeaders.Add("Origin", "https://unrelated.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync("api/start", null)).StatusCode);
        browser.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await browser.PostAsync("api/start", null)).StatusCode);
    }

    [Fact]
    public async Task PrivateStatusRequiresSameUserGrantAndRejectsHostileWebsites()
    {
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("api/open", null)).StatusCode);
        var url = await DashboardCompanion.OpenAsync(Options);
        var grant = new { ticket = url.Fragment[1..] };
        var response = await browser.PostAsJsonAsync("api/session", grant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var session = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session").GetString();
        browser.DefaultRequestHeaders.Add("Cookie", "stowhaven-dashboard=" + session);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/status")).StatusCode);
        browser.DefaultRequestHeaders.Remove("Cookie");
        browser.DefaultRequestHeaders.Authorization = new("Bearer", session);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("api/session", grant)).StatusCode);
        using var status = await browser.GetAsync("api/status");
        Assert.Contains("no-store", status.Headers.CacheControl!.ToString());
        Assert.Equal("empty", (await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        browser.DefaultRequestHeaders.Add("Origin", "https://unrelated.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("api/history")).StatusCode);
        browser.DefaultRequestHeaders.Remove("Origin");
        browser.DefaultRequestHeaders.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("api/status")).StatusCode);
        browser.DefaultRequestHeaders.Host = null;
        browser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("api/status")).StatusCode);
    }

    [Fact]
    public async Task AuthorizationExpiresAfterTwelveInactiveHoursAndCompanionRestart()
    {
        var clock = new DashboardClock();
        var companion = await DashboardCompanion.StartAsync(Options, clock);
        using var browser = Browser(companion);
        try
        {
            await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
            clock.Advance(TimeSpan.FromHours(11));
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("api/status")).StatusCode);
            clock.Advance(TimeSpan.FromHours(2));
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/status")).StatusCode);
            await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
            clock.Advance(TimeSpan.FromHours(11));
            Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("api/activity", null)).StatusCode);
            clock.Advance(TimeSpan.FromHours(2));
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("api/status")).StatusCode);
            clock.Advance(TimeSpan.FromHours(10));
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/status")).StatusCode);
            await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
            using var response = await browser.GetAsync("api/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var previousUrl = await DashboardCompanion.OpenAsync(Options);
            await companion.DisposeAsync();
            companion = await DashboardCompanion.StartAsync(new DashboardOptions { ReportingDirectory = _root, Port = browser.BaseAddress!.Port }, clock);
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/status")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("api/session", new { ticket = previousUrl.Fragment[1..] })).StatusCode);
            await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("api/status")).StatusCode);
        }
        finally { await companion.DisposeAsync(); }
    }

    [Fact]
    public async Task UnsupportedSchemaIsUnavailableAndPreservedWithoutServingItsPayload()
    {
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        var path = Path.Combine(_root, "snapshot.json");
        const string future = "{\"schemaVersion\":999,\"credentials\":\"SYNTHETIC-SECRET\"}";
        await File.WriteAllTextAsync(path, future);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var text = await browser.GetStringAsync("api/status");
        Assert.Contains("incompatible", text);
        Assert.Contains("Upgrade", text);
        Assert.DoesNotContain("SYNTHETIC-SECRET", text);
        Assert.Equal(future, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReportingAndAuthorizationFilesArePrivateAndSymlinkStorageIsRejected()
    {
        await using var companion = await DashboardCompanion.StartAsync(Options);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_root, "companion.json")));
        File.SetUnixFileMode(Path.Combine(_root, "companion.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DashboardCompanion.OpenAsync(Options));
        var alias = _root + "-alias";
        Directory.CreateSymbolicLink(alias, _root);
        try { await Assert.ThrowsAsync<IOException>(() => DashboardCompanion.StartAsync(new DashboardOptions { ReportingDirectory = alias })); }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public async Task InvalidSnapshotNeverBecomesAReportedCleanCheck()
    {
        await using var companion = await DashboardCompanion.StartAsync(Options);
        using var browser = Browser(companion);
        await DashboardTestAuthorization.AuthorizeAsync(browser, Options);
        var path = Path.Combine(_root, "snapshot.json");
        foreach (var malformed in new[] { "{", "null", "{\"schemaVersion\":1}", "{\"schemaVersion\":\"future\"}", JsonSerializer.Serialize(new { schemaVersion = 1, attemptId = Guid.NewGuid(), result = "cleanCheck", targetNames = new[] { "documents" }, selectionFingerprint = new string('a', 64), filesScanned = 0, serverBackupEvidence = "none" }) })
        {
            await File.WriteAllTextAsync(path, malformed);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var response = await browser.GetAsync("api/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("cleanCheck", json);
            Assert.True(json.Contains("unavailable") || json.Contains("incompatible"));
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class DashboardClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
