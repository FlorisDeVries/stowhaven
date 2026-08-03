using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using FlorisDeV.BackupClient.Clients.BackupApi.Config;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Refit;

namespace FlorisDeV.BackupClient.Clients.BackupApi;

public static class HostBuilderExtensions
{
    public static void AddBackupApi(this IServiceCollection services,
        IConfiguration configuration,
        Action<BackupApiClientOptions>? configureApiOptions = null,
        string configSectionName = BackupApiClientOptions.DefaultSectionName,
        string retrySectionName = BackupApiClientOptions.DefaultRetrySectionName)
    {
        // configuration
        var clientConfigSection = configuration.GetSection(configSectionName);

        services.AddOptions<BackupApiClientOptions>()
            .Bind(clientConfigSection)
            .PostConfigure(o => configureApiOptions?.Invoke(o))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiUrl), "ApiUrl must be configured.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.AuthenticationScope), "AuthenticationScope must be configured.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.AuthenticationTenant), "AuthenticationTenant must be configured.")
            .Validate(o => !o.WakeUp.Enabled || o.WakeUp.InitialDelaySeconds > 0, "WakeUp:InitialDelaySeconds must be greater than zero.")
            .Validate(o => !o.WakeUp.Enabled || o.WakeUp.MaxDelaySeconds > 0, "WakeUp:MaxDelaySeconds must be greater than zero.")
            .Validate(o => !o.WakeUp.Enabled || o.WakeUp.MaxDelaySeconds >= o.WakeUp.InitialDelaySeconds, "WakeUp:MaxDelaySeconds must be greater than or equal to WakeUp:InitialDelaySeconds.")
            .Validate(o => !o.WakeUp.Enabled || o.WakeUp.MaxWaitSeconds > 0, "WakeUp:MaxWaitSeconds must be greater than zero.")
            .Validate(o => !o.WakeUp.Enabled || o.WakeUp.ProbeTimeoutSeconds > 0, "WakeUp:ProbeTimeoutSeconds must be greater than zero.")
            .Validate(o => o.WakeUp.RecheckIntervalSeconds >= 0, "WakeUp:RecheckIntervalSeconds cannot be negative.")
            .ValidateOnStart();

        var retryConfigSection = configuration.GetSection(retrySectionName);

        if (!retryConfigSection.Exists() || !retryConfigSection.GetChildren().Any())
        {
            throw new InvalidOperationException(
                $"Retry configuration section '{retrySectionName}' is missing or empty. " +
                "Please ensure your appsettings contains something like:\n" +
                $"\"{retrySectionName}:MaxRetryAttempts\": 3");
        }

        // Require a TokenCredential to be registered by the consumer
        if (services.All(d => d.ServiceType != typeof(TokenCredential)))
        {
            throw new InvalidOperationException(
                """
                No TokenCredential found in the DI container.
                Please register one before calling AddRouttyCloudClient, for example:

                services.AddSingleton<TokenCredential>(_ => CreateAzureCredentials(isDevelopment, configuration));

                or, for default Azure behavior:

                services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
                """);
        }

        services.TryAddSingleton<IApiWakeUpService, ApiWakeUpService>();
        services.TryAddTransient<BackupApiWakeUpHandler>();
        services.TryAddTransient<BackupApiAuthHandler>();

        // The wake-up probe deliberately bypasses the Refit pipeline. Running it through the same
        // handler would recurse, and running it through standard retries would multiply two separate
        // retry loops. This anonymous client is used only for /api/health/alive.
        services.AddHttpClient(ApiWakeUpService.HttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<BackupApiClientOptions>>();
            client.BaseAddress = new Uri(options.Value.ApiUrl.TrimEnd('/'));
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Add client(s) with resilience. By default, the following outcomes are handled:
        //
        // - Any status code 500 or above.
        // - 429(Too Many Requests).
        // - 408(Request Timeout).
        // - Exceptions: HttpRequestException and TimeoutRejectedException.
        //
        // To customize the retry you should call .Configure(options => ...)
        //   options.Retry.ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
        //       .Handle<TimeoutRejectedException>()
        //       .Handle<HttpRequestException>()
        //       .HandleResult(response => response.StatusCode == HttpStatusCode.InternalServerError ...)
        //
        // Ref: https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience?tabs=dotnet-cli
        //      https://devblogs.microsoft.com/dotnet/building-resilient-cloud-services-with-dotnet-8/
        services
            .AddRefitClient<IBackupApiClient>(new RefitSettings
            {
                ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() }
                })
            })
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<BackupApiClientOptions>>();
                var backupOptions = provider.GetRequiredService<IOptions<FlorisDeV.BackupClient.Config.BackupClientOptions>>();

                client.BaseAddress = new Uri(options.Value.ApiUrl.TrimEnd('/'));
                client.Timeout = TimeSpan.FromSeconds(backupOptions.Value.HttpTimeoutSeconds);
            })
            .AddHttpMessageHandler<BackupApiWakeUpHandler>()
            .AddHttpMessageHandler<BackupApiAuthHandler>()
            .RedactLoggedHeaders([ HeaderNames.Authorization ])
            .AddStandardResilienceHandler(retryConfigSection);
    }
}
