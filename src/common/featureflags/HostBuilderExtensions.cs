using Azure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.FeatureFilters;

namespace FlorisDeV.FeatureFlags;

public static class HostBuilderExtensions
{
    private static readonly AzureAppConfigOptions AppConfigOptions = new();

    /// <summary>
    ///    Enabled feature flags and dynamic configuration using Azure App Configuration service
    /// </summary>
    /// <remarks>
    ///   https://docs.microsoft.com/en-us/azure/azure-app-configuration/enable-dynamic-configuration-aspnet-core?tabs=core5x
    ///   https://docs.microsoft.com/en-us/azure/azure-app-configuration/quickstart-aspnet-core-app?tabs=core6x
    ///   https://docs.microsoft.com/en-us/azure/azure-app-configuration/quickstart-feature-flag-aspnet-core?tabs=core6x%2Ccore5x
    /// </remarks>
    /// <param name="builder"></param>
    public static void AddAzureFeatureFlags(this WebApplicationBuilder builder)
    {
        builder.Configuration.GetSection(AzureAppConfigOptions.SectionName).Bind(AppConfigOptions);

        if (!string.IsNullOrWhiteSpace(AppConfigOptions.ConnectionEndpoint))
        {
            var environmentName = AppConfigOptions.EnvironmentName ?? builder.Environment.EnvironmentName;

            builder.Services.AddSingleton(builder.Configuration);
            builder.Services.AddAzureAppConfiguration();

            builder.Configuration.AddAzureAppConfiguration(options =>
            {
                if (Uri.TryCreate(AppConfigOptions.ConnectionEndpoint, UriKind.Absolute, out var endpointUri))
                {
                    options.Connect(endpointUri, new DefaultAzureCredential());
                }
                else
                {
                    options.Connect(AppConfigOptions.ConnectionEndpoint);
                }

                if (!string.IsNullOrEmpty(AppConfigOptions.TrimKeyPrefix))
                {
                    options.TrimKeyPrefix(AppConfigOptions.TrimKeyPrefix);
                }

                options
                    .Select(KeyFilter.Any)
                    .Select(KeyFilter.Any, environmentName)
                    .UseFeatureFlags(flagOptions =>
                    {
                        flagOptions.Select(KeyFilter.Any);
                        flagOptions.Select(KeyFilter.Any, environmentName);
                        flagOptions.SetRefreshInterval(AppConfigOptions.FeaturesLifetime);
                    });

                if (AppConfigOptions.RefreshInterval > TimeSpan.Zero)
                {
                    options.ConfigureRefresh(refreshOptions =>
                    {
                        refreshOptions.Register(AppConfigOptions.SentinelKey, true);
                        refreshOptions.SetRefreshInterval(AppConfigOptions.RefreshInterval);
                    });
                }
            });
        }

        builder.Services.AddFeatureManagement()
                        .AddFeatureFilter<TimeWindowFilter>();
    }

    /// <summary>
    ///    Adds dynamic configuration refresh support
    /// </summary>
    public static void UseAzureFeatureFlags(this IApplicationBuilder app)
    {
        if (!string.IsNullOrWhiteSpace(AppConfigOptions.ConnectionEndpoint))
        {
            app.UseAzureAppConfiguration();
        }
    }
}