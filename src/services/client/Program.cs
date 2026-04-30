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

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "FlorisDeV Backup Client";
    })
    .ConfigureAppConfiguration((context, configuration) =>
    {
        configuration.AddUserSecrets(Assembly.GetExecutingAssembly());
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationServices();
        services.AddApplicationConfigurations(context.Configuration, context.HostingEnvironment);
        services.AddBackupApi(context.Configuration);
    })
    .AddOpenTelemetry(
        context => TelemetryProvider.CreateResourceAttributes(context.HostingEnvironment.EnvironmentName),
        TelemetryProvider.SourceName,
        TelemetryProvider.SourceName)
    .Build();

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
    Console.WriteLine($"Application terminated unexpectedly: {e}");
    throw;
}
finally
{
    // Ensure all telemetry is flushed before shutdown
    await host.StopAsync();
    host.Dispose();
}