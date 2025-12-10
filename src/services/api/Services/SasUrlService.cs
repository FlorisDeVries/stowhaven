using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Dapr.Client;
using FlorisDeV.BackupApi.Models;

namespace FlorisDeV.BackupApi.Services;

public interface ISasUrlService
{
    Task<SasUrl> GenerateUploadSasUrlAsync(string path, int? ttlMinutes = null);
    Task<SasUrl> GenerateDownloadSasUrlAsync(string path, int? ttlMinutes = null);
}

public class SasUrl
{
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public int TtlMinutes { get; set; }
}

public class SasUrlService(ILogger<SasUrlService> logger, DaprClient daprClient) : ISasUrlService
{

    private readonly string _secretStoreName = "secret-store";
    private BlobServiceClient? _blobServiceClient;
    private string? _dataStorageAccount;
    private string? _dataContainer;

    private async Task<BlobServiceClient> GetBlobServiceClientAsync()
    {
        if (_blobServiceClient != null)
        {
            return _blobServiceClient;
        }

        // Get storage configuration from DAPR secrets
        _dataStorageAccount = await GetSecretAsync("storage-account-name")
                              ?? Environment.GetEnvironmentVariable("DATA_STORAGE_ACCOUNT")
                              ?? throw new InvalidOperationException("DATA_STORAGE_ACCOUNT not found in secrets or environment");

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

    public async Task<SasUrl> GenerateUploadSasUrlAsync(string path, int? ttlMinutes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var ttl = ttlMinutes ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);

        try
        {
            var blobServiceClient = await GetBlobServiceClientAsync();
            var containerClient = blobServiceClient.GetBlobContainerClient(_dataContainer);
            var blobClient = containerClient.GetBlobClient(path);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _dataContainer,
                BlobName = path,
                Resource = "b", // blob
                ExpiresOn = expiresAt,
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5) // Allow 5 minutes clock skew
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

            var sasUrl = blobClient.GenerateSasUri(sasBuilder).ToString();
            var response = new SasUrl
            {
                Url = sasUrl,
                ExpiresAt = expiresAt
            };

            logger.LogInformation("Generated upload SAS URL for path: {Path}, expires at: {ExpiresAt}", path, expiresAt);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating upload SAS URL for path: {Path}", path);
            throw;
        }
    }

    public async Task<SasUrl> GenerateDownloadSasUrlAsync(string path, int? ttlMinutes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var ttl = ttlMinutes ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);

        try
        {
            var blobServiceClient = await GetBlobServiceClientAsync();
            var containerClient = blobServiceClient.GetBlobContainerClient(_dataContainer);
            var blobClient = containerClient.GetBlobClient(path);

            // Check if blob exists
            var exists = await blobClient.ExistsAsync();
            if (!exists.Value)
            {
                throw new FileNotFoundException($"Blob not found: {path}");
            }

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _dataContainer,
                BlobName = path,
                Resource = "b", // blob
                ExpiresOn = expiresAt,
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5) // Allow 5 minutes clock skew
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sasUrl = blobClient.GenerateSasUri(sasBuilder).ToString();
            var response = new SasUrl
            {
                Url = sasUrl,
                ExpiresAt = expiresAt
            };

            logger.LogInformation("Generated download SAS URL for path: {Path}, expires at: {ExpiresAt}", path, expiresAt);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating download SAS URL for path: {Path}", path);
            throw;
        }
    }
}
