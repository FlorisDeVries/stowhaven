using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Telemetry;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FlorisDeV.BackupClient.Tests.ProcessFixture;

// Test-only process. Every path must be within the caller's synthetic temporary scenario.
public static class FixtureProgram
{
    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        if (!root.StartsWith(Path.GetTempPath(), StringComparison.Ordinal)) throw new ArgumentException("Temporary scenario required.");
        if (args.ElementAtOrDefault(3) == "systemdProbe")
        {
            try { await new SystemdUserService().ShowAsync("stowhaven-synthetic-never-start.service", default); Console.WriteLine("unexpectedConnected"); return 1; }
            catch (SystemdControlException ex) { Console.WriteLine(ex.Category); return 0; }
        }
        var database = Path.GetFullPath(args[1]);
        var directory = Path.GetDirectoryName(database)!;
        var resolvedDirectory = new DirectoryInfo(directory).ResolveLinkTarget(true)?.FullName ?? directory;
        var resolvedDatabase = (File.Exists(database) ? new FileInfo(database).ResolveLinkTarget(true)?.FullName : null) ?? Path.Combine(resolvedDirectory, Path.GetFileName(database));
        if (!resolvedDatabase.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Database must belong to this temporary scenario.");
        var label = args[2];
        var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
        using var state = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = database }), NullLogger<BackupStateService>.Instance);
        var api = new Mock<IBackupApiClient>();
        api.Setup(x => x.RegisterDevice(It.IsAny<RegisterDeviceRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (RegisterDeviceRequest request, CancellationToken token) =>
            {
                // SQLite has already opened and closed connections when registration is reached.
                await File.WriteAllTextAsync(Path.Combine(root, label + ".entered"), "entered", token);
                while (!File.Exists(Path.Combine(root, label + ".release"))) await Task.Delay(20, token);
                return new DeviceRegistrationResponse { DeviceId = request.DeviceId!.Value, TenantId = "test", UserId = "test", DisplayName = "fixture", Status = DeviceRegistrationStatus.Active, CreatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
            });
        using var telemetry = new TelemetryProvider();
        var service = new BackupService(NullLogger<BackupService>.Instance, telemetry, api.Object, state,
            new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state, NullLogger<BackupScanner>.Instance),
            new Mock<IFileUploader>().Object, Options.Create(new BackupClientOptions { BackupTargets = new() { ["test"] = target }, Dashboard = new() { Enabled = args.ElementAtOrDefault(3) != "unreported", ReportingDirectory = Path.Combine(root, "reporting") } }));
        try
        {
            if (args.ElementAtOrDefault(3) == "request")
            {
                var invocation = Guid.NewGuid().ToString("N");
                Environment.SetEnvironmentVariable("INVOCATION_ID", invocation);
                var control = new DashboardStartControl(new() { FilePath = database }, new()
                {
                    BackupTargets = new(), Dashboard = new() { Enabled = true, ReportingDirectory = Path.Combine(root, "reporting") }
                }, new RequestFixture(root, label, invocation, async () => { await service.RunAsync(default); }), TimeSpan.FromSeconds(2));
                Console.WriteLine((await control.StartAsync()).State); return 0;
            }
            Console.WriteLine((await service.RunAsync(default)).Outcome);
            if (args.ElementAtOrDefault(3) == "linger")
            {
                await File.WriteAllTextAsync(Path.Combine(root, label + ".finished"), "finished");
                while (!File.Exists(Path.Combine(root, label + ".exit"))) await Task.Delay(20);
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex.GetType().Name); return 10; }
    }
    private sealed class RequestFixture(string root, string label, string invocation, Func<Task> run) : ISystemdUserService
    {
        public Task<SystemdServiceState> ShowAsync(string unit, CancellationToken token) => Task.FromResult(new SystemdServiceState("loaded", "inactive", "oneshot", invocation));
        public async Task StartAsync(string unit, CancellationToken token)
        {
            await File.WriteAllTextAsync(Path.Combine(root, label + ".requested"), "requested", token);
            while (!File.Exists(Path.Combine(root, label + ".launch"))) await Task.Delay(20, token);
            await run();
        }
    }
}
