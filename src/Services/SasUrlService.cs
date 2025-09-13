using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Azure.Identity;
using BackupApi.Models;

namespace BackupApi.Services;

public class SasUrlService : ISasUrlService
{
    private readonly ILogger<SasUrlService> _logger;
    private readonly string _dataStorageAccount;
    private readonly string _dataContainer;
    private readonly BlobServiceClient _blobServiceClient;

    public SasUrlService(ILogger<SasUrlService> logger)
    {
        _logger = logger;
        _dataStorageAccount = Environment.GetEnvironmentVariable("DATA_STORAGE_ACCOUNT") 
            ?? throw new InvalidOperationException("DATA_STORAGE_ACCOUNT environment variable is required");
        _dataContainer = Environment.GetEnvironmentVariable("DATA_CONTAINER") ?? "backups";

        // Use managed identity for authentication
        var credential = new DefaultAzureCredential();
        var blobServiceUri = new Uri($"https://{_dataStorageAccount}.blob.core.windows.net");
        _blobServiceClient = new BlobServiceClient(blobServiceUri, credential);
    }

    public async Task<SasResponse> GenerateUploadSasUrlAsync(string path, int? ttlMinutes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var ttl = ttlMinutes ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);

        try
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(_dataContainer);
            var blobClient = containerClient.GetBlobClient(path);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _dataContainer,
                BlobName = path,
                Resource = "b", // blob
                ExpiresOn = expiresAt
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

            var sasUri = blobClient.GenerateSasUri(sasBuilder);

            _logger.LogInformation("Generated upload SAS URL for blob: {BlobName}, expires at: {ExpiresAt}", 
                path, expiresAt);

            return new SasResponse
            {
                Url = sasUri.ToString(),
                ExpiresAt = expiresAt,
                TtlMinutes = ttl
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate upload SAS URL for blob: {BlobName}", path);
            throw;
        }
    }

    public async Task<SasResponse> GenerateDownloadSasUrlAsync(string path, int? ttlMinutes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var ttl = ttlMinutes ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);

        try
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(_dataContainer);
            var blobClient = containerClient.GetBlobClient(path);

            // Check if blob exists
            var exists = await blobClient.ExistsAsync();
            if (!exists.Value)
            {
                throw new ArgumentException($"Blob '{path}' does not exist");
            }

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _dataContainer,
                BlobName = path,
                Resource = "b", // blob
                ExpiresOn = expiresAt
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sasUri = blobClient.GenerateSasUri(sasBuilder);

            _logger.LogInformation("Generated download SAS URL for blob: {BlobName}, expires at: {ExpiresAt}", 
                path, expiresAt);

            return new SasResponse
            {
                Url = sasUri.ToString(),
                ExpiresAt = expiresAt,
                TtlMinutes = ttl
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate download SAS URL for blob: {BlobName}", path);
            throw;
        }
    }
}
