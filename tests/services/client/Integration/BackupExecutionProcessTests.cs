using System.Diagnostics;
using FlorisDeV.BackupClient.Tests.ProcessFixture;

namespace FlorisDeV.BackupClient.Tests.Integration;

public sealed partial class BackupExecutionProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stowhaven-execution-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = [];
    private readonly Dictionary<string, Process> _processLabels = new();

    [Theory]
    [InlineData("same")]
    [InlineData("hardlink")]
    [InlineData("symlink")]
    [InlineData("parentSymlink")]
    [InlineData("relative")]
    public async Task CompetingAttemptsCannotOverlapAfterSqliteClosesConnectionsAndDeathReleasesOwnership(string aliasKind)
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "state.db");
        var owner = Start(database, "owner");
        await WaitForFile("owner.entered");
        var alias = database;
        if (aliasKind == "hardlink")
        {
            alias = Path.Combine(_root, "hardlink.db");
            var link = Process.Start(new ProcessStartInfo("ln") { ArgumentList = { database, alias } })!;
            await link.WaitForExitAsync(); Assert.Equal(0, link.ExitCode); link.Dispose();
        }
        if (aliasKind == "symlink") { alias = Path.Combine(_root, "symlink.db"); File.CreateSymbolicLink(alias, database); }
        if (aliasKind == "parentSymlink")
        {
            var directoryAlias = _root + "-alias";
            Directory.CreateSymbolicLink(directoryAlias, _root);
            alias = Path.Combine(directoryAlias, "state.db");
        }
        if (aliasKind == "relative") alias = Path.GetRelativePath(Environment.CurrentDirectory, database);
        var competitor = Start(alias, "competitor");
        await Task.Delay(500);
        Assert.False(File.Exists(Path.Combine(_root, "competitor.entered")), "A duplicate must not reach backup work.");
        Assert.True(competitor.HasExited, "A duplicate must return immediately, without waiting or queueing.");
        Assert.Contains("BackupAlreadyRunningException", await competitor.StandardOutput.ReadToEndAsync());
        owner.Kill(entireProcessTree: true); await owner.WaitForExitAsync();
        var later = Start(database, "later");
        await WaitForFile("later.entered");
        await File.WriteAllTextAsync(Path.Combine(_root, "later.release"), "release");
        await later.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, later.ExitCode);
        Assert.Contains("NoChanges", await later.StandardOutput.ReadToEndAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequentialAttemptsReleaseOwnershipWhileAnInheritedDescriptorRemainsOpen(bool startRequest)
    {
        Directory.CreateDirectory(_root);
        var child = Start(Path.Combine(_root, "state.db"), "sequential", mode: startRequest ? "retainedReservation" : "retainedDescriptor");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var output = await child.StandardOutput.ReadToEndAsync();
        Assert.True(child.ExitCode == 0, output + await child.StandardError.ReadToEndAsync());
        Assert.Equal(startRequest ? new[] { "started", "started" } : new[] { "NoChanges", "NoChanges" },
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private Process Start(string database, string label, string? invocationId = null, string? mode = null, bool unavailableUserBus = false)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "FlorisDeV.BackupClient.Tests.runtimeconfig.json"), "--depsfile", Path.Combine(AppContext.BaseDirectory, "FlorisDeV.BackupClient.Tests.deps.json"), typeof(FixtureProgram).Assembly.Location, _root, database, label }) start.ArgumentList.Add(arg);
        if (mode != null) start.ArgumentList.Add(mode);
        if (invocationId != null) start.Environment["INVOCATION_ID"] = invocationId;
        else start.Environment.Remove("INVOCATION_ID");
        if (unavailableUserBus)
        {
            start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=" + Path.Combine(_root, "nonexistent-bus");
            start.Environment["XDG_RUNTIME_DIR"] = _root;
        }
        var process = Process.Start(start)!; _children.Add(process); _processLabels[label] = process; return process;
    }
    private async Task WaitForFile(string file)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(Path.Combine(_root, file)))
        {
            if (_processLabels.TryGetValue(file.Split('.')[0], out var process) && process.HasExited)
                throw new InvalidOperationException("Fixture exited before observation: " + await process.StandardError.ReadToEndAsync() + await process.StandardOutput.ReadToEndAsync());
            await Task.Delay(20, timeout.Token);
        }
    }
    public void Dispose()
    {
        foreach (var child in _children) { if (!child.HasExited) child.Kill(entireProcessTree: true); child.WaitForExit(); child.Dispose(); }
        if (Directory.Exists(_root + "-alias")) Directory.Delete(_root + "-alias");
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
