using System.Diagnostics;
using Dapr.Client;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Constants;
using FlorisDeV.Logging.OpenTelemetry;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupApi.Services;

public interface ISecretService
{
    Task<string?> GetSecretAsync(string secretName);
    Task<string?> GetRequiredSecretAsync(string secretName);
}

public partial class SecretService(
    DaprClient daprClient,
    IConfiguration configuration,
    ILogger<SecretService> logger,
    TelemetryProvider telemetry
) : ISecretService
{
    public async Task<string?> GetSecretAsync(string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        using var activity = telemetry.ActivitySource.StartActivity("GetSecret");
        activity?.SetTag(ActivityAttributes.SecretName, secretName);
        activity?.SetTag(ActivityAttributes.SecretStoreComponent, DaprComponents.SecretStore);

        try
        {
            var configurationValue = configuration[secretName];
            if (!string.IsNullOrWhiteSpace(configurationValue))
            {
                telemetry.SecretRetrievals.Add(1, new TagList { { "secret_store", "configuration" }, { "result", "found" } });
                return configurationValue;
            }

            var secrets = await daprClient.GetSecretAsync(DaprComponents.SecretStore, secretName);
            if (secrets.TryGetValue(secretName, out var value))
            {
                telemetry.SecretRetrievals.Add(1, new TagList { { "secret_store", DaprComponents.SecretStore }, { "result", "found" } });
                return value;
            }

            telemetry.SecretRetrievals.Add(1, new TagList { { "secret_store", DaprComponents.SecretStore }, { "result", "not_found" } });
            LogSecretNotFound(logger, secretName);
            return null;
        }
        catch (Exception ex)
        {
            telemetry.SecretRetrievals.Add(1, new TagList { { "secret_store", DaprComponents.SecretStore }, { "result", "error" }, { "error.type", ex.GetType().Name } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(ActivityAttributes.ErrorType, ex.GetType().Name);
            activity?.AddException(ex);

            LogSecretStoreUnavailable(logger, DaprComponents.SecretStore, secretName, ex);

            throw new SecretStoreUnavailableException(
                DaprComponents.SecretStore,
                ex);
        }
    }

    public async Task<string?> GetRequiredSecretAsync(string secretName)
    {
        var value = await GetSecretAsync(secretName);

        if (string.IsNullOrWhiteSpace(value))
            throw new SecretNotFoundException(
                DaprComponents.SecretStore,
                secretName);

        return value;
    }

    #region Logging

    [LoggerMessage(LogLevel.Warning, "Secret '{secretName}' not found")]
    static partial void LogSecretNotFound(ILogger logger, string secretName);

    [LoggerMessage(LogLevel.Error, "Secret store '{secretStore}' unavailable while retrieving '{secretName}'")]
    static partial void LogSecretStoreUnavailable(ILogger logger, string secretStore, string secretName, Exception ex);

    #endregion
}