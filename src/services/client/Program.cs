using System.Reflection;
using System.Diagnostics;
using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Telemetry;
using FlorisDeV.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Observation commands deliberately bypass the backup host, credentials and resident scheduler.
if (args.FirstOrDefault() is "dashboard" or "open-dashboard")
{
    // The installed companion shares the client's configuration via a symlink.
    // Watch its target directory so atomic client-config replacements reload.
    var localConfiguration = new FileInfo(Path.Combine(AppContext.BaseDirectory, "appsettings.local.json"));
    var localConfigurationPath = localConfiguration.FullName;
    if (localConfiguration.Exists && localConfiguration.ResolveLinkTarget(returnFinalTarget: true) is { } target)
        localConfigurationPath = target.FullName;
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddJsonFile(localConfigurationPath, optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args.Skip(1).Where(argument => argument != "--print-url").ToArray())
        .Build();
    var options = configuration.GetSection("BackupClient:Dashboard").Get<DashboardOptions>() ?? new();
    try
    {
        if (args[0] == "dashboard")
        {
            await using var companion = await DashboardCompanion.StartAsync(options, startControl: new DashboardStartControl(
                configuration.GetSection("Database").Get<FlorisDeV.BackupClient.Config.DatabaseOptions>() ?? new(),
                configuration.GetSection("BackupClient").Get<FlorisDeV.BackupClient.Config.BackupClientOptions>() ?? new() { BackupTargets = new() }),
                currentOptions: () => configuration.GetSection("BackupClient").Get<FlorisDeV.BackupClient.Config.BackupClientOptions>() ?? new() { BackupTargets = new() });
            Console.WriteLine($"Stowhaven dashboard companion listening at {companion.Address}. Run open-dashboard to authorize your browser.");
            await companion.WaitForShutdownAsync();
        }
        else
        {
            var url = await DashboardCompanion.OpenAsync(options);
            if (args.Contains("--print-url", StringComparer.Ordinal)) Console.WriteLine(url);
            else Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or System.ComponentModel.Win32Exception or NotSupportedException or TaskCanceledException)
    {
        Console.Error.WriteLine($"Dashboard unavailable ({ex.GetType().Name}). Start the companion in this user account and check its private reporting directory. Use open-dashboard --print-url when no browser launcher is available.");
        Environment.ExitCode = 1;
    }
    return;
}

var logFilePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "backup-client", "logs", "backup-client-.log");

var command = args.FirstOrDefault();
var allowInteractiveAuthentication =
    command?.Equals("configure", StringComparison.OrdinalIgnoreCase) == true ||
    command?.Equals("login", StringComparison.OrdinalIgnoreCase) == true;

var resultArgument = Array.FindIndex(args, argument => argument.Equals("--result-file", StringComparison.OrdinalIgnoreCase));
string? resultFile = null;
var hostArguments = args;
if (resultArgument >= 0)
{
    if (resultArgument + 1 >= args.Length || args[resultArgument + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException("--result-file requires a file path.");
    resultFile = args[resultArgument + 1];
    hostArguments = args.Where((_, index) => index != resultArgument && index != resultArgument + 1).ToArray();
}

var host = Host.CreateDefaultBuilder(hostArguments)
    // Resolve appsettings.json/appsettings.local.json next to the executable, regardless of the
    // caller's current directory (matters for the PATH-linked binary and for install.sh, which
    // exec's the installed exe without changing into its directory first).
    .UseContentRoot(AppContext.BaseDirectory)
    .UseWindowsService(options =>
    {
        // Compatibility identifier for existing Windows Service registrations.
        options.ServiceName = "FlorisDeV Backup Client";
    })
    .AddSerilog("backup-client", logFilePath)
    .ConfigureAppConfiguration((context, configuration) =>
    {
        if (context.HostingEnvironment.IsDevelopment())
        {
            configuration.AddUserSecrets(Assembly.GetExecutingAssembly());
        }

        // Per-machine overrides (backup targets, etc.) written by the "configure" command.
        // Not a publish content item, so it survives re-publishing the exe in place.
        configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
        if (resultFile != null)
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["BackupClient:ResultFilePath"] = resultFile });
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationServices();
        services.AddApplicationConfigurations(
            context.Configuration,
            context.HostingEnvironment,
            allowInteractiveAuthentication);
        services.AddBackupApi(context.Configuration);
    })
    .AddOpenTelemetry(
        context => TelemetryProvider.CreateResourceAttributes(context.HostingEnvironment.EnvironmentName),
        TelemetryProvider.SourceName,
        TelemetryProvider.SourceName)
    .Build();

if (args.FirstOrDefault()?.Equals("configure", StringComparison.OrdinalIgnoreCase) == true)
{
    var configureOptions = new ConfigureOptions(
        SkipTargets: args.Contains("--skip-targets", StringComparer.OrdinalIgnoreCase),
        SkipLogin: args.Contains("--skip-login", StringComparer.OrdinalIgnoreCase),
        SkipAccessCheck: args.Contains("--skip-access-check", StringComparer.OrdinalIgnoreCase));

    await host.StartAsync();

    try
    {
        await host.Services.GetRequiredService<IClientSetupService>().ConfigureAsync(configureOptions, CancellationToken.None);
    }
    catch (Exception e)
    {
        host.Services.GetRequiredService<ILogger<Program>>().LogCritical(e, "Setup failed");
        throw;
    }
    finally
    {
        await host.StopAsync();
        host.Dispose();
    }

    return;
}

if (args.FirstOrDefault()?.Equals("login", StringComparison.OrdinalIgnoreCase) == true)
{
    await host.StartAsync();

    try
    {
        await host.Services.GetRequiredService<IClientSetupService>().LoginAsync(CancellationToken.None);
    }
    catch (Exception e)
    {
        host.Services.GetRequiredService<ILogger<Program>>().LogCritical(e, "Login failed");
        throw;
    }
    finally
    {
        await host.StopAsync();
        host.Dispose();
    }

    return;
}

var scheduleEnabled = host.Services.GetRequiredService<IConfiguration>()
    .GetValue<bool>($"{FlorisDeV.BackupClient.Config.BackupClientOptions.SectionName}:Schedule:Enabled");

if (scheduleEnabled)
{
    await host.RunAsync();
    return;
}

await host.StartAsync();

resultFile ??= host.Services.GetRequiredService<IConfiguration>()["BackupClient:ResultFilePath"];
try
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Application starting...");

    // Log startup configuration for troubleshooting
    host.LogStartupConfiguration();

    using var tokenSource = new CancellationTokenSource();
    var result = command?.Equals("restore", StringComparison.OrdinalIgnoreCase) == true
        ? new BackupResult { Operation = "restore", Outcome = await host.Services.GetRequiredService<IRestoreService>().RestoreAsync(tokenSource.Token)
            ? BackupOutcome.Succeeded : BackupOutcome.Failed }
        : await host.Services.GetRequiredService<IBackupService>().RunAsync(tokenSource.Token);

    await result.WriteAsync(resultFile, tokenSource.Token);
    Environment.ExitCode = result.ExitCode;
    logger.LogInformation("Operation outcome {Outcome}; run {RunId}, commit {CommitId}, exit code {ExitCode}", result.Outcome, result.RunId, result.CommitId, result.ExitCode);
}
catch (Exception e)
{
    host.Services.GetRequiredService<ILogger<Program>>()
        .LogCritical(e, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
    try
    {
        await new BackupResult { Operation = command?.Equals("restore", StringComparison.OrdinalIgnoreCase) == true ? "restore" : "backup",
            Outcome = BackupOutcome.Failed, ErrorType = e.GetType().Name }.WriteAsync(resultFile);
    }
    catch (Exception reportError) when (reportError is IOException or UnauthorizedAccessException)
    {
        host.Services.GetRequiredService<ILogger<Program>>().LogError(reportError, "Could not write the operation result file");
    }
}
finally
{
    // Ensure all telemetry is flushed before shutdown
    await host.StopAsync();
    host.Dispose();
}
