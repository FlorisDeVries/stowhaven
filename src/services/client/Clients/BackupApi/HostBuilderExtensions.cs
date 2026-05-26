using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using FlorisDeV.BackupClient.Clients.BackupApi.Config;
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

        services.TryAddTransient<BackupApiAuthHandler>();

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
            .AddHttpMessageHandler<BackupApiAuthHandler>()
            .RedactLoggedHeaders([ HeaderNames.Authorization ])
            .AddStandardResilienceHandler(retryConfigSection);
    }
}