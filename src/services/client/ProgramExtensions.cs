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
        services.AddSingleton<IBackupScanner, BackupScanner>();
        services.AddSingleton<IBackupEncryptionService, BackupEncryptionService>();
        services.AddSingleton<IFileUploader, FileUploader>();
        services.AddTransient<IApiWakeUpService, ApiWakeUpService>();
        services.AddTransient<IClientSetupService, ClientSetupService>();
        services.AddTransient<IBackupService, BackupService>();
        services.AddTransient<IRestoreService, RestoreService>();
    }

    public static void AddApplicationConfigurations(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // BackupTargets emptiness is intentionally NOT validated here: BackupClientOptions is resolved
        // for unrelated purposes too (e.g. HttpClient timeout in HostBuilderExtensions.AddBackupApi),
        // which would otherwise block bootstrap commands like "login"/"configure" that run before any
        // targets are configured. BackupService.GetEffectiveTargets() enforces this where it matters.
        services.AddOptions<BackupClientOptions>()
            .Bind(configuration.GetSection(BackupClientOptions.SectionName))
            .Validate(o => o.Schedule.IntervalMinutes > 0, "BackupClient:Schedule:IntervalMinutes must be greater than zero")
            .ValidateOnStart();

        if (configuration.GetValue<bool>($"{BackupClientOptions.SectionName}:Schedule:Enabled"))
        {
            services.AddHostedService<ScheduledBackupWorker>();
        }

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
            var apiConfig = provider.GetRequiredService<IOptions<BackupApiClientOptions>>().Value;

            // Use no-op only when the API is a local endpoint (local dev with anonymous auth disabled).
            // If the URL points at a deployed host, use real MSAL auth even in Development.
            var apiUri = Uri.TryCreate(apiConfig.ApiUrl, UriKind.Absolute, out var u) ? u : null;
            var isLocalApi = apiUri is not null && (apiUri.IsLoopback || apiUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

            if (environment.IsDevelopment() && isLocalApi)
            {
                logger.LogInformation("Development mode (local API): Using NoOpTokenCredential (authentication disabled)");
                return new NoOpTokenCredential();
            }

            logger.LogInformation("Using MsalTokenCredential for {ApiUrl} (interactive authentication)", apiConfig.ApiUrl);
            var azureAdConfig = provider.GetRequiredService<IOptions<AzureAdOptions>>().Value;

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