using System.Reflection;
using FlorisDeV.BackupClient;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Telemetry;
using FlorisDeV.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var logFilePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "backup-client", "logs", "backup-client-.log");

var command = args.FirstOrDefault();
var allowInteractiveAuthentication =
    command?.Equals("configure", StringComparison.OrdinalIgnoreCase) == true ||
    command?.Equals("login", StringComparison.OrdinalIgnoreCase) == true;

var host = Host.CreateDefaultBuilder(args)
    // Resolve appsettings.json/appsettings.local.json next to the executable, regardless of the
    // caller's current directory (matters for the PATH-linked binary and for install.sh, which
    // exec's the installed exe without changing into its directory first).
    .UseContentRoot(AppContext.BaseDirectory)
    .UseWindowsService(options =>
    {
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

try
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Application starting...");

    // Log startup configuration for troubleshooting
    host.LogStartupConfiguration();

    var tokenSource = new CancellationTokenSource();
    var result = args.FirstOrDefault()?.Equals("restore", StringComparison.OrdinalIgnoreCase) == true
        ? await host.Services.GetRequiredService<IRestoreService>().RestoreAsync(tokenSource.Token)
        : await host.Services.GetRequiredService<IBackupService>().Backup(tokenSource.Token);

    logger.LogInformation("Operation completed with result: {Result}", result);
}
catch (Exception e)
{
    host.Services.GetRequiredService<ILogger<Program>>()
        .LogCritical(e, "Application terminated unexpectedly");
    throw;
}
finally
{
    // Ensure all telemetry is flushed before shutdown
    await host.StopAsync();
    host.Dispose();
}
