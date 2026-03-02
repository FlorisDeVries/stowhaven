using Azure.Core;
using Azure.Identity;
using FlorisDeV.BackupClient.Authentication;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Clients.BackupApi.Config;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient;

public static class ProgramExtensions
{
    public static void AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<TelemetryProvider>();
        services.AddSingleton<ResiliencePipelineProvider>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IBackupStateService, BackupStateService>();
        services.AddSingleton<BackupDeltaComputer>();
        services.AddSingleton<IBackupScanner, BackupScanner>();
        services.AddSingleton<IFileUploader, FileUploader>();
        services.AddTransient<IBackupService, BackupService>();
    }

    public static void AddApplicationConfigurations(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<BackupClientOptions>()
            .Bind(configuration.GetSection(BackupClientOptions.SectionName))
            .Validate(o => o.BackupTargets.Count > 0, "BackupClient:BackupTargets must contain at least one target")
            .ValidateOnStart();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<AzureAdOptions>()
            .Bind(configuration.GetSection(AzureAdOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Instance), "AzureAd:Instance is required")
            .Validate(o => !string.IsNullOrWhiteSpace(o.TenantId), "AzureAd:TenantId is required")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId), "AzureAd:ClientId is required")
            .ValidateOnStart();

        services.AddSingleton<TokenCredential>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<Program>>();
            
            if (environment.IsDevelopment())
            {
                logger.LogInformation("Development mode: Using NoOpTokenCredential (authentication disabled)");
                // Development: API disables authentication, so use no-op credential
                return new NoOpTokenCredential();
            }

            logger.LogInformation("Production mode: Using MsalTokenCredential (interactive authentication)");
            // Production: Use MSAL for interactive user authentication (distributed clients)
            var azureAdConfig = provider.GetRequiredService<IOptions<AzureAdOptions>>().Value;
            var apiConfig = provider.GetRequiredService<IOptions<BackupApiClientOptions>>().Value;

            // MSAL credential creation is async, but we need sync registration
            // So we create it synchronously here (blocking is acceptable at startup)
            var credential = MsalTokenCredential.CreateAsync(
                clientId: azureAdConfig.ClientId,
                tenantId: azureAdConfig.TenantId,
                scopes: [apiConfig.AuthenticationScope],
                authority: azureAdConfig.Instance
            ).GetAwaiter().GetResult();

            return credential;
        });
    }
}