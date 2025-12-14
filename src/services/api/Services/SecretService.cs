using Dapr.Client;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Exceptions;

namespace FlorisDeV.BackupApi.Services;

public interface ISecretService
{
    Task<string?> GetSecretAsync(string secretName);
    Task<string?> GetRequiredSecretAsync(string secretName);
}

public class SecretService(
    DaprClient daprClient,
    ILogger<SecretService> logger
) : ISecretService
{
    public async Task<string?> GetSecretAsync(string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        try
        {
            var secrets = await daprClient.GetSecretAsync(DaprComponents.SecretStore, secretName);
            if (secrets.TryGetValue(secretName, out var value))
                return value;

            logger.LogWarning("Secret '{SecretName}' not found", secretName);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Secret store '{SecretStore}' unavailable while retrieving '{SecretName}'",
                DaprComponents.SecretStore,
                secretName);

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
}