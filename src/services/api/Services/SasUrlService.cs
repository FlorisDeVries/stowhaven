using System.Diagnostics;
using System.Security;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.Logging.OpenTelemetry;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupApi.Services;

public interface ISasUrlService
{
    Task<SasUrlInfo> GenerateUploadSasUrlAsync(string path, string? clientIp = null, int? ttlMinutes = null,
        CancellationToken cancellationToken = default);

    Task<SasUrlInfo> GenerateReadSasUrlAsync(string path, string? clientIp = null, int? ttlMinutes = null,
        CancellationToken cancellationToken = default);
}

public partial class SasUrlService(
    ILogger<SasUrlService> logger,
    IBlobStorageService blobStorageService,
    TelemetryProvider telemetry
) : ISasUrlService
{

    public async Task<SasUrlInfo> GenerateUploadSasUrlAsync(string path, string? clientIp = null, int? ttlMinutes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var activity = telemetry.ActivitySource.StartActivity("GenerateUploadSasUrl");
        var stopwatch = Stopwatch.StartNew();

        // Validate path
        path = ValidatePath(path);
        activity?.SetTag(ActivityAttributes.SasUrlPath, path);

        if (!string.IsNullOrEmpty(clientIp))
        {
            activity?.SetTag("sas.client_ip", clientIp);
        }

        var ttl = ttlMinutes ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);
        activity?.SetTag(ActivityAttributes.SasUrlTtlMinutes, ttl);

        var metricTags = new TagList { { "operation", "generate_upload_sas" } };

        try
        {
            var blobServiceClient = await blobStorageService.GetBlobServiceClientAsync(cancellationToken);
            var storageAccount = await blobStorageService.GetStorageAccountNameAsync(cancellationToken);
            var containerName = await blobStorageService.GetContainerNameAsync(cancellationToken);
            var isAzurite = await blobStorageService.IsUsingAzuriteAsync(cancellationToken);

            activity?.SetTag(ActivityAttributes.StorageAccount, storageAccount);

            Uri sasUrl;

            if (isAzurite)
            {
                // LOCAL DEVELOPMENT (Azurite): directory SAS (Resource="d") requires ADLS Gen2
                // hierarchical namespace, which Azurite does not support — the SDK silently
                // downgrades to Resource="b" (blob), binding the signature to the exact path,
                // which then breaks when the client appends file names.
                // Fall back to a container-level SAS (Resource="c") for local dev only.
                // This is intentionally broader than production.
                var containerSasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = containerName,
                    Resource          = "c",
                    ExpiresOn         = expiresAt,
                    StartsOn          = DateTimeOffset.UtcNow.AddMinutes(-5),
                    Protocol          = SasProtocol.HttpsAndHttp // Azurite serves HTTP only
                };
                containerSasBuilder.SetPermissions(BlobContainerSasPermissions.Create | BlobContainerSasPermissions.Write);

                // Add IP restriction if client IP is provided
                if (!string.IsNullOrEmpty(clientIp))
                {
                    containerSasBuilder.IPRange = new SasIPRange(System.Net.IPAddress.Parse(clientIp));
                    LogSasWithIpRestriction(logger, clientIp);
                }

                // Use the blob container client which already has credentials
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
                // Container-level URL — path is NOT embedded; the client uses BasePath separately.
                // GenerateSasUri returns the complete URI including the leading '?' before the query string.
                sasUrl = containerClient.GenerateSasUri(containerSasBuilder);
            }
            else
            {
                // PRODUCTION (Azure Storage with HNS / ADLS Gen2): use a directory-scoped SAS
                // (Resource="d") so the token is cryptographically bound to the staging path and
                // cannot be used to read or overwrite blobs outside of it.
                var dirSasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = containerName,
                    BlobName          = path,
                    Resource          = "d",
                    ExpiresOn         = expiresAt,
                    StartsOn          = DateTimeOffset.UtcNow.AddMinutes(-5),
                    Protocol          = SasProtocol.Https
                };
                dirSasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

                // Add IP restriction if client IP is provided
                if (!string.IsNullOrEmpty(clientIp))
                {
                    dirSasBuilder.IPRange = new SasIPRange(System.Net.IPAddress.Parse(clientIp));
                    LogSasWithIpRestriction(logger, clientIp);
                }

                // Use User Delegation Key for enhanced security (no account key exposure)
                var key = await blobStorageService.GetUserDelegationKeyAsync(expiresAt, cancellationToken);
                var sasToken = dirSasBuilder.ToSasQueryParameters(key, storageAccount).ToString();

                // Directory-level URL — path IS embedded so BlobContainerClient resolves correctly
                sasUrl = new Uri($"{blobServiceClient.Uri}/{containerName}/{path}?{sasToken}");
            }

            var result = new SasUrlInfo
            {
                Url        = sasUrl,
                ExpiresAt  = expiresAt,
                TtlMinutes = ttl,
                BasePath   = path,
                IsPathEmbedded = !isAzurite
            };

            stopwatch.Stop();
            telemetry.SasUrlsGenerated.Add(1, metricTags);
            telemetry.SasUrlTtl.Record(ttl, metricTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var errorTags = new TagList
            {
                { "operation", "generate_upload_sas" },
                { "error.type", ex.GetType().Name }
            };
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, errorTags);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(ActivityAttributes.OperationStatus, "error");
            activity?.SetTag(ActivityAttributes.ErrorType, ex.GetType().Name);
            activity?.SetTag(ActivityAttributes.ErrorMessage, ex.Message);
            activity?.AddException(ex);

            LogErrorGeneratingSasUrl(logger, path, ex);
            throw;
        }
    }

    public async Task<SasUrlInfo> GenerateReadSasUrlAsync(string path, string? clientIp = null, int? ttlMinutes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var activity = telemetry.ActivitySource.StartActivity("GenerateReadSasUrl");
        var stopwatch = Stopwatch.StartNew();

        path = ValidateReadPath(path);
        activity?.SetTag(ActivityAttributes.SasUrlPath, path);

        var ttl = ttlMinutes ?? 60;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);
        var metricTags = new TagList { { "operation", "generate_read_sas" } };

        try
        {
            var blobServiceClient = await blobStorageService.GetBlobServiceClientAsync(cancellationToken);
            var storageAccount = await blobStorageService.GetStorageAccountNameAsync(cancellationToken);
            var containerName = await blobStorageService.GetContainerNameAsync(cancellationToken);
            var isAzurite = await blobStorageService.IsUsingAzuriteAsync(cancellationToken);

            Uri sasUrl;

            if (isAzurite)
            {
                var containerSasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = containerName,
                    Resource          = "c",
                    ExpiresOn         = expiresAt,
                    StartsOn          = DateTimeOffset.UtcNow.AddMinutes(-5),
                    Protocol          = SasProtocol.HttpsAndHttp
                };
                containerSasBuilder.SetPermissions(BlobContainerSasPermissions.Read);

                if (!string.IsNullOrEmpty(clientIp))
                {
                    containerSasBuilder.IPRange = new SasIPRange(System.Net.IPAddress.Parse(clientIp));
                    LogSasWithIpRestriction(logger, clientIp);
                }

                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
                // GenerateSasUri returns the complete URI including the leading '?' before the query string.
                sasUrl = containerClient.GenerateSasUri(containerSasBuilder);
            }
            else
            {
                var dirSasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = containerName,
                    BlobName          = path,
                    Resource          = "d",
                    ExpiresOn         = expiresAt,
                    StartsOn          = DateTimeOffset.UtcNow.AddMinutes(-5),
                    Protocol          = SasProtocol.Https
                };
                dirSasBuilder.SetPermissions(BlobSasPermissions.Read);

                if (!string.IsNullOrEmpty(clientIp))
                {
                    dirSasBuilder.IPRange = new SasIPRange(System.Net.IPAddress.Parse(clientIp));
                    LogSasWithIpRestriction(logger, clientIp);
                }

                var key = await blobStorageService.GetUserDelegationKeyAsync(expiresAt, cancellationToken);
                var sasToken = dirSasBuilder.ToSasQueryParameters(key, storageAccount).ToString();
                sasUrl = new Uri($"{blobServiceClient.Uri}/{containerName}/{path}?{sasToken}");
            }

            var result = new SasUrlInfo
            {
                Url = sasUrl,
                ExpiresAt = expiresAt,
                TtlMinutes = ttl,
                BasePath = path,
                IsPathEmbedded = !isAzurite
            };

            stopwatch.Stop();
            telemetry.SasUrlsGenerated.Add(1, metricTags);
            telemetry.SasUrlTtl.Record(ttl, metricTags);
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, metricTags);
            activity?.SetTag(ActivityAttributes.OperationStatus, "success");

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            telemetry.OperationDuration.Record(stopwatch.ElapsedMilliseconds, new TagList
            {
                { "operation", "generate_read_sas" },
                { "error.type", ex.GetType().Name }
            });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(ActivityAttributes.OperationStatus, "error");
            activity?.AddException(ex);

            LogErrorGeneratingSasUrl(logger, path, ex);
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

        if (!path.StartsWith("staging/", StringComparison.Ordinal) &&
            !path.StartsWith("runs/", StringComparison.Ordinal))
            throw new SecurityException("Upload SAS may only target staging/ or runs/");

        if (path.Contains('.', StringComparison.Ordinal))
            throw new SecurityException("Upload SAS must target a directory, not a blob");

        return path;
    }

    private static string ValidateReadPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be null or whitespace", nameof(path));

        path = path.Trim('/');

        if (path.Contains("..", StringComparison.Ordinal))
            throw new SecurityException("Invalid path traversal");

        if (!path.StartsWith("devices/", StringComparison.Ordinal))
            throw new SecurityException("Read SAS may only target devices/");

        if (!path.EndsWith("/files", StringComparison.Ordinal))
            throw new SecurityException("Read SAS must target a device files directory");

        return path;
    }

    #region Logging

    [LoggerMessage(LogLevel.Error, "Error generating upload SAS URL for path: {path}")]
    static partial void LogErrorGeneratingSasUrl(ILogger logger, string path, Exception ex);

    [LoggerMessage(LogLevel.Information, "Generated SAS URL with IP restriction: {clientIp}")]
    static partial void LogSasWithIpRestriction(ILogger logger, string clientIp);

    #endregion
}
