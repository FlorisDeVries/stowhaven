using System.Reflection;
using FlorisDeV.BackupClient;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Telemetry;
using FlorisDeV.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, configuration) =>
    {
        configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
        configuration.AddJsonFile("appsettings.json", false, true);
        configuration.AddUserSecrets(Assembly.GetExecutingAssembly());
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationServices();
        services.AddApplicationConfigurations(context.Configuration);
    })
    .AddOpenTelemetry(
        context => TelemetryProvider.CreateResourceAttributes(context.HostingEnvironment.EnvironmentName),
        TelemetryProvider.SourceName,
        TelemetryProvider.SourceName)
    .Build();

await host.StartAsync();

try
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Application starting...");
    
    // Log startup configuration for troubleshooting
    host.LogStartupConfiguration();

    // Application Code - no scope needed with Transient services
    var tokenSource = new CancellationTokenSource();
    var backupService = host.Services.GetRequiredService<IBackupService>();
    var result = await backupService.Backup(tokenSource.Token);

    logger.LogInformation("Backup completed with result: {Result}", result);
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