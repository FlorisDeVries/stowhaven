using Microsoft.Extensions.Diagnostics.HealthChecks;
using Azure.Storage.Blobs;
using Azure.Identity;

public class StorageHealthCheck : IHealthCheck
{
    private readonly ILogger<StorageHealthCheck> _logger;

    public StorageHealthCheck(ILogger<StorageHealthCheck> logger)
    {
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var dataStorageAccount = Environment.GetEnvironmentVariable("DATA_STORAGE_ACCOUNT");
            var dataContainer = Environment.GetEnvironmentVariable("DATA_CONTAINER") ?? "backups";

            if (string.IsNullOrEmpty(dataStorageAccount))
            {
                return HealthCheckResult.Unhealthy("DATA_STORAGE_ACCOUNT environment variable is not configured");
            }

            var credential = new DefaultAzureCredential();
            var blobServiceUri = new Uri($"https://{dataStorageAccount}.blob.core.windows.net");
            var blobServiceClient = new BlobServiceClient(blobServiceUri, credential);

            var containerClient = blobServiceClient.GetBlobContainerClient(dataContainer);
            
            // Try to get container properties to verify connectivity
            var response = await containerClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            
            _logger.LogInformation("Storage health check passed for container: {Container}", dataContainer);
            
            return HealthCheckResult.Healthy($"Successfully connected to storage container: {dataContainer}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Storage health check failed");
            return HealthCheckResult.Unhealthy($"Storage health check failed: {ex.Message}");
        }
    }
}
