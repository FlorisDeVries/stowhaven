using System.Security;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Dapr.Client;
using FlorisDeV.BackupApi.Models.Infrastructure;

namespace FlorisDeV.BackupApi.Services;

public interface ISasUrlService
{
    Task<SasUrlInfo> GenerateUploadSasUrlAsync(string path, int? ttlMinutes = null,
        CancellationToken cancellationToken = default);
}

public class SasUrlService(ILogger<SasUrlService> logger, DaprClient daprClient) : ISasUrlService
{
    private readonly string _secretStoreName = "secret-store";
    private BlobServiceClient? _blobServiceClient;
    private string _dataStorageAccount = null!;
    private string _dataContainer = null!;
    private UserDelegationKey? _cachedKey;
    private DateTimeOffset _keyExpiresAt;

    public async Task<SasUrlInfo> GenerateUploadSasUrlAsync(string path, int? ttlMinutes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Validate path
        path = ValidatePath(path);

        var ttl = ttlMinutes ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);

        try
        {
            var blobServiceClient = await GetBlobServiceClientAsync();
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _dataContainer,
                BlobName = path,
                Resource = "d", // directory
                ExpiresOn = expiresAt,
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // Allow 5 minutes clock skew
                Protocol = SasProtocol.Https
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

            // Use User Delegation Key for enhanced security (no account key exposure)
            var key = await GetDelegationKeyAsync(blobServiceClient, expiresAt, cancellationToken);
            var sasToken = sasBuilder.ToSasQueryParameters(
                key,
                _dataStorageAccount
            ).ToString();

            var result = new SasUrlInfo
            {
                Url = new Uri(
                    $"https://{_dataStorageAccount}.blob.core.windows.net/{_dataContainer}/{path}?{sasToken}"),
                ExpiresAt = expiresAt,
                TtlMinutes = ttl
            };

            logger.LogInformation("Generated upload SAS URL for path: {Path}, expires at: {ExpiresAt}", path,
                expiresAt);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating upload SAS URL for path: {Path}", path);
            throw;
        }
    }

    private string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be null or whitespace", nameof(path));

        path = path.Trim('/');

        if (path.Contains("..", StringComparison.Ordinal))
            throw new SecurityException("Invalid path traversal");

        if (!path.StartsWith("staging/", StringComparison.Ordinal))
            throw new SecurityException("Upload SAS may only target staging/");

        if (path.Contains('.', StringComparison.Ordinal))
            throw new SecurityException("Upload SAS must target a directory, not a blob");

        return path;
    }

    private async Task<BlobServiceClient> GetBlobServiceClientAsync()
    {
        if (_blobServiceClient != null)
        {
            return _blobServiceClient;
        }

        // Get storage configuration from DAPR secrets
        _dataStorageAccount = await GetSecretAsync("storage-account-name")
                              ?? Environment.GetEnvironmentVariable("DATA_STORAGE_ACCOUNT")
                              ?? throw new InvalidOperationException(
                                  "DATA_STORAGE_ACCOUNT not found in secrets or environment");

        _dataContainer = await GetSecretAsync("data-container")
                         ?? Environment.GetEnvironmentVariable("DATA_CONTAINER")
                         ?? "backups";

        // Use managed identity for authentication
        var credential = new DefaultAzureCredential();
        var blobServiceUri = new Uri($"https://{_dataStorageAccount}.blob.core.windows.net");
        _blobServiceClient = new BlobServiceClient(blobServiceUri, credential);

        return _blobServiceClient;
    }

    private async Task<string?> GetSecretAsync(string secretName)
    {
        try
        {
            var secret = await daprClient.GetSecretAsync(_secretStoreName, secretName);
            return secret.TryGetValue(secretName, out var value) ? value : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get secret {SecretName} from DAPR secret store", secretName);
            return null;
        }
    }

    private async Task<UserDelegationKey> GetDelegationKeyAsync(
        BlobServiceClient client,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (_cachedKey != null && _keyExpiresAt > expiresAt.AddMinutes(5))
            return _cachedKey;

        var keyExpiry = DateTimeOffset.UtcNow.AddHours(2);
        _cachedKey = await client.GetUserDelegationKeyAsync(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            keyExpiry, cancellationToken);

        _keyExpiresAt = keyExpiry;
        return _cachedKey;
    }
}