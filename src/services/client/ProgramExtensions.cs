using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlorisDeV.BackupClient;

public static class ProgramExtensions
{
    public static void AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<TelemetryProvider>();

        services.AddTransient<IBackupService, BackupService>();
    }

    public static void AddApplicationConfigurations(this IServiceCollection services, IConfiguration configuration)
    {
    }
}