using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Azure.Identity;
using BackupApi.Models;
using Dapr.Client;
using System.Text.Json;

namespace BackupApi.Services;

public class DaprSasUrlService : ISasUrlService
{
    private readonly ILogger<DaprSasUrlService> _logger;
    private readonly DaprClient _daprClient;
    private readonly string _stateStoreName = "statestore";
    private readonly string _secretStoreName = "azurekeyvault";
    private BlobServiceClient? _blobServiceClient;
    private string? _dataStorageAccount;
    private string? _dataContainer;

    public DaprSasUrlService(ILogger<DaprSasUrlService> logger, DaprClient daprClient)
    {
        _logger = logger;
        _daprClient = daprClient;
    }

    private async Task<BlobServiceClient> GetBlobServiceClientAsync()
    {
        if (_blobServiceClient == null)
        {
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
        }

        return _blobServiceClient;
    }

    private async Task<string?> GetSecretAsync(string secretName)
    {
        try
        {
            var secret = await _daprClient.GetSecretAsync(_secretStoreName, secretName);
            return secret.TryGetValue(secretName, out var value) ? value : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get secret {SecretName} from DAPR secret store", secretName);
            return null;
        }
    }

    public async Task<SasResponse> GenerateUploadSasUrlAsync(string path, int? ttlMinutes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var ttl = ttlMinutes ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);
        var cacheKey = $"upload-sas:{path}:{ttl}";

        // Try to get cached SAS URL from DAPR state store
        var cachedSas = await GetCachedSasUrlAsync(cacheKey);
        if (cachedSas != null && cachedSas.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            _logger.LogInformation("Returning cached upload SAS URL for path: {Path}", path);
            return cachedSas;
        }

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
            var response = new SasResponse
            {
                Url = sasUrl,
                ExpiresAt = expiresAt
            };

            // Cache the SAS URL in DAPR state store
            await CacheSasUrlAsync(cacheKey, response, TimeSpan.FromMinutes(ttl - 5));

            // Publish event about SAS URL generation
            await PublishSasGeneratedEventAsync("upload", path, expiresAt);

            _logger.LogInformation("Generated upload SAS URL for path: {Path}, expires at: {ExpiresAt}", path, expiresAt);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating upload SAS URL for path: {Path}", path);
            throw;
        }
    }

    public async Task<SasResponse> GenerateDownloadSasUrlAsync(string path, int? ttlMinutes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var ttl = ttlMinutes ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);
        var cacheKey = $"download-sas:{path}:{ttl}";

        // Try to get cached SAS URL from DAPR state store
        var cachedSas = await GetCachedSasUrlAsync(cacheKey);
        if (cachedSas != null && cachedSas.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            _logger.LogInformation("Returning cached download SAS URL for path: {Path}", path);
            return cachedSas;
        }

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
            var response = new SasResponse
            {
                Url = sasUrl,
                ExpiresAt = expiresAt
            };

            // Cache the SAS URL in DAPR state store
            await CacheSasUrlAsync(cacheKey, response, TimeSpan.FromMinutes(ttl - 5));

            // Publish event about SAS URL generation
            await PublishSasGeneratedEventAsync("download", path, expiresAt);

            _logger.LogInformation("Generated download SAS URL for path: {Path}, expires at: {ExpiresAt}", path, expiresAt);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating download SAS URL for path: {Path}", path);
            throw;
        }
    }

    private async Task<SasResponse?> GetCachedSasUrlAsync(string cacheKey)
    {
        try
        {
            var cachedData = await _daprClient.GetStateAsync<string>(_stateStoreName, cacheKey);
            if (!string.IsNullOrEmpty(cachedData))
            {
                return JsonSerializer.Deserialize<SasResponse>(cachedData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cached SAS URL from state store with key: {CacheKey}", cacheKey);
        }

        return null;
    }

    private async Task CacheSasUrlAsync(string cacheKey, SasResponse sasResponse, TimeSpan ttl)
    {
        try
        {
            var serializedData = JsonSerializer.Serialize(sasResponse);
            await _daprClient.SaveStateAsync(_stateStoreName, cacheKey, serializedData, metadata: new Dictionary<string, string>
            {
                ["ttlInSeconds"] = ((int)ttl.TotalSeconds).ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache SAS URL in state store with key: {CacheKey}", cacheKey);
        }
    }

    private async Task PublishSasGeneratedEventAsync(string operation, string path, DateTimeOffset expiresAt)
    {
        try
        {
            var eventData = new
            {
                Operation = operation,
                Path = path,
                ExpiresAt = expiresAt,
                GeneratedAt = DateTimeOffset.UtcNow,
                Service = "backup-api"
            };

            await _daprClient.PublishEventAsync("backup-pubsub", "sas-generated", eventData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish SAS generated event for {Operation} operation on path: {Path}", operation, path);
        }
    }
}
