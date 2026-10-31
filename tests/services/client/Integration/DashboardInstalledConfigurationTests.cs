using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FlorisDeV.BackupClient.Dashboard;

namespace FlorisDeV.BackupClient.Tests.Integration;

public partial class BackupServiceIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledObserverReloadsAtomicEditsToSharedConfigurationWithoutCreatingBackupState(bool relativeTarget)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "stowhaven-installed-observer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process? child = null;
        try
        {
            var payload = Directory.CreateDirectory(Path.Combine(root, "payload")).FullName;
            var client = Directory.CreateDirectory(Path.Combine(root, "client")).FullName;
            // Only build artifacts go into this private observer publication.
            // No installed configuration, state or user secrets are copied/read.
            foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
                if (source.EndsWith(".dll", StringComparison.Ordinal) || source.EndsWith(".deps.json", StringComparison.Ordinal) || source.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
                    File.Copy(source, Path.Combine(payload, Path.GetFileName(source)));
            await File.WriteAllTextAsync(Path.Combine(payload, "appsettings.json"), "{}");
            var config = Path.Combine(client, "appsettings.local.json");
            var reporting = Path.Combine(root, "reporting");
            var database = Path.Combine(root, "never-created.db");
            var target = Directory.CreateDirectory(Path.Combine(client, "target")).FullName;
            var targetIgnore = Path.Combine(target, ".backupignore");
            if (relativeTarget) await File.WriteAllTextAsync(targetIgnore, "");
            var clientIgnore = Path.Combine(client, ".backupignore");
            await File.WriteAllTextAsync(clientIgnore, "*.tmp\n");
            await File.WriteAllTextAsync(Path.Combine(payload, ".backupignore"), "*.tmp\n");
            var producerOptions = new FlorisDeV.BackupClient.Config.BackupClientOptions
            {
                BackupTargets = new() { ["documents"] = target }, IgnoreFilePath = clientIgnore,
                Dashboard = new() { Enabled = true, ReportingDirectory = reporting, MaximumBackupAge = TimeSpan.FromDays(1) }
            };
            await CreateFreshnessService(producerOptions, TimeProvider.System).RunAsync(default);
            async Task Configure(bool resident)
            {
                var temporary = config + ".new";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
                {
                    BackupClient = new
                    {
                        Dashboard = new { Enabled = true, ReportingDirectory = reporting, MaximumBackupAge = "1.00:00:00",
                            LogDirectory = Path.Combine(root, "logs"), BackupServiceUnit = "stowhaven-synthetic-reload.service",
                            BackupTimerUnit = "stowhaven-synthetic-reload.timer" },
                        Schedule = new { Enabled = resident }, BackupTargets = new Dictionary<string, string> { ["documents"] = relativeTarget ? "target" : target }, IgnoreFilePath = ".backupignore"
                    },
                    Database = new { FilePath = database }
                }));
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, config, overwrite: true);
            }
            await Configure(false);
            File.CreateSymbolicLink(Path.Combine(payload, "appsettings.local.json"), config);
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = client, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(payload, "FlorisDeV.BackupClient.dll"));
            start.ArgumentList.Add("dashboard"); // Observer-only; never invoke the backup host.
            start.Environment.Clear();
            foreach (var name in new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["XDG_DATA_HOME"] = Path.Combine(root, "data");
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(root, "config");
            start.Environment["XDG_RUNTIME_DIR"] = Path.Combine(root, "missing-runtime");
            start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=" + Path.Combine(root, "missing-bus");
            child = Process.Start(start)!;
            _ = child.StandardOutput.ReadToEndAsync();
            _ = child.StandardError.ReadToEndAsync();
            var options = new DashboardOptions { Enabled = true, ReportingDirectory = reporting };
            Uri? opened = null;
            for (var retry = 0; retry < 100 && opened == null; retry++)
            {
                try { opened = await DashboardCompanion.OpenAsync(options); }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { await Task.Delay(100); }
            }
            Assert.NotNull(opened);
            using var browser = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(opened.GetLeftPart(UriPartial.Authority)) };
            var grant = await browser.PostAsJsonAsync("/api/session", new { ticket = opened.Fragment[1..] });
            grant.EnsureSuccessStatusCode();
            var session = (await grant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session").GetString();
            browser.DefaultRequestHeaders.Authorization = new("Bearer", session);
            async Task ExpectScheduling(string expected)
            {
                string? actual = null;
                for (var retry = 0; retry < 50; retry++)
                {
                    actual = (await browser.GetFromJsonAsync<JsonElement>("/api/status")).GetProperty("scheduling").GetProperty("state").GetString();
                    if (actual == expected) return;
                    await Task.Delay(100);
                }
                Assert.Equal(expected, actual);
            }
            await ExpectScheduling("userSessionUnavailable");
            var initial = await browser.GetFromJsonAsync<DashboardStatus>("/api/status");
            Assert.Equal("fresh", initial!.Freshness!.State);
            // An empty local override initially agrees with missing copied rules, so
            // only editing the real client's local file exposes wrong-base reads.
            await File.WriteAllTextAsync(relativeTarget ? targetIgnore : clientIgnore, "*.log\n");
            var edited = await browser.GetFromJsonAsync<DashboardStatus>("/api/status");
            Assert.Equal("configurationChanged", edited!.Freshness!.State);
            await Configure(true);
            await ExpectScheduling("incompatibleSchedule");
            await Configure(false);
            await ExpectScheduling("userSessionUnavailable");
            Assert.False(File.Exists(database));
        }
        finally
        {
            if (child != null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                child.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
