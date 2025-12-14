using Azure.Storage.Blobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlorisDeV.HealthChecks;

/// <summary>
/// Health check for Azure Blob Storage connectivity
/// </summary>
public class AzureBlobStorageHealthCheck(BlobServiceClient blobServiceClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Try to get account info to verify connectivity
            var accountInfo = await blobServiceClient
                .GetAccountInfoAsync(cancellationToken)
                .ConfigureAwait(false);

            var data = new Dictionary<string, object>
            {
                { "AccountKind", accountInfo.Value.AccountKind.ToString() },
                { "SkuName", accountInfo.Value.SkuName.ToString() }
            };

            return HealthCheckResult.Healthy(
                "Azure Blob Storage is accessible.",
                data);
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Azure Blob Storage is not accessible.",
                ex);
        }
    }
}
