using System.Security;
using Azure.Identity;
using Azure.Storage;
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

public class SasUrlService(
    ILogger<SasUrlService> logger,
    ISecretService secretService
) : ISasUrlService
{
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
            var blobServiceClient = await GetBlobServiceClientAsync(cancellationToken);
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

            string sasToken;
            if (await UsingAzurite(cancellationToken))
            {
                // Use Account Key for Azurite (local development)
                var accountKey = await secretService.GetRequiredSecretAsync("DATA_STORAGE_ACCOUNT_KEY");
                var credential = new StorageSharedKeyCredential(_dataStorageAccount, accountKey);

                sasToken = sasBuilder.ToSasQueryParameters(
                    credential
                ).ToString();
            }
            else
            {
                // Use User Delegation Key for enhanced security (no account key exposure)
                var key = await GetDelegationKeyAsync(blobServiceClient, expiresAt, cancellationToken);
                sasToken = sasBuilder.ToSasQueryParameters(
                    key,
                    _dataStorageAccount
                ).ToString();
            }

            var result = new SasUrlInfo
            {
                Url = new Uri($"{blobServiceClient.Uri}/{_dataContainer}/{path}?{sasToken}"),
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

    private async Task<BlobServiceClient> GetBlobServiceClientAsync(CancellationToken cancellationToken = default)
    {
        if (_blobServiceClient != null)
        {
            return _blobServiceClient;
        }

        // Get storage configuration from DAPR secrets
        _dataStorageAccount = await secretService.GetRequiredSecretAsync("DATA_STORAGE_ACCOUNT")
                              ?? throw new InvalidOperationException(
                                  "DATA_STORAGE_ACCOUNT not found in secrets or environment");

        _dataContainer = await secretService.GetRequiredSecretAsync("DATA_CONTAINER")
                         ?? throw new InvalidOperationException(
                             "DATA_CONTAINER not found in secrets or environment");

        if (await UsingAzurite(cancellationToken))
        {
            // LOCAL DEVELOPMENT: Account-key auth
            var accountKey = await secretService.GetRequiredSecretAsync("DATA_STORAGE_ACCOUNT_KEY");
            var credential = new StorageSharedKeyCredential(_dataStorageAccount, accountKey);
            var blobEndpoint = await secretService.GetRequiredSecretAsync("DATA_STORAGE_BLOB_ENDPOINT");

            _blobServiceClient = new BlobServiceClient(new Uri(blobEndpoint!), credential);
        }
        else
        {
            // Azure
            var credential = new DefaultAzureCredential();
            var blobServiceUri = new Uri($"https://{_dataStorageAccount}.blob.core.windows.net");
            _blobServiceClient = new BlobServiceClient(blobServiceUri, credential);
        }

        return _blobServiceClient;
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

    private async Task<bool> UsingAzurite(CancellationToken cancellationToken = default)
    {
        var useAzurite = await secretService.GetRequiredSecretAsync("USE_AZURITE");
        return bool.TryParse(useAzurite, out var result) && result;
    }
}