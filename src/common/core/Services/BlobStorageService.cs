using System.Diagnostics;
using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Sas;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.Logging.OpenTelemetry;
using DataLakeUserDelegationKey = Azure.Storage.Files.DataLake.Models.UserDelegationKey;

namespace FlorisDeV.BackupApi.Services;

/// <summary>
/// Service for accessing Azure Blob Storage with automatic configuration
/// for both local development (Azurite) and production (Azure).
/// </summary>
public interface IBlobStorageService
{
    /// <summary>
    /// Gets a BlobServiceClient configured for the current environment.
    /// </summary>
    Task<BlobServiceClient> GetBlobServiceClientAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a BlobContainerClient for the data container.
    /// </summary>
    Task<BlobContainerClient> GetContainerClientAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a DataLakeServiceClient for ADLS Gen2 operations (Azure only).
    /// </summary>
    Task<DataLakeServiceClient> GetDataLakeServiceClientAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user delegation key for generating SAS tokens (Azure only).
    /// </summary>
    Task<UserDelegationKey> GetUserDelegationKeyAsync(DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user delegation key for generating directory-scoped (ADLS Gen2) SAS tokens (Azure only).
    /// </summary>
    Task<DataLakeUserDelegationKey> GetDataLakeUserDelegationKeyAsync(DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the storage account name.
    /// </summary>
    Task<string> GetStorageAccountNameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the data container name.
    /// </summary>
    Task<string> GetContainerNameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if running in local development mode (Azurite).
    /// </summary>
    Task<bool> IsUsingAzuriteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a blob from source to destination, using rename (ADLS Gen2) or copy+delete.
    /// </summary>
    Task MoveBlobAsync(
        string sourceBlobName,
        string destinationBlobName,
        Dictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default);
}

public partial class BlobStorageService(
    ISecretService secretService,
    ILogger<BlobStorageService> logger,
    TelemetryProvider telemetry
) : IBlobStorageService
{
    private const string AllowCopyDeleteFallbackSecretName = "ALLOW_COPY_DELETE_FALLBACK";
    private const string AllowCopyDeleteFallbackConfigurationKey = "Storage:AllowCopyDeleteFallback";

    private BlobServiceClient? _blobServiceClient;
    private string? _storageAccountName;
    private string? _containerName;
    private bool? _isUsingAzurite;
    private UserDelegationKey? _cachedDelegationKey;
    private DateTimeOffset _delegationKeyExpiresAt;
    private DataLakeUserDelegationKey? _cachedDataLakeDelegationKey;
    private DateTimeOffset _dataLakeDelegationKeyExpiresAt;

    public async Task<BlobServiceClient> GetBlobServiceClientAsync(CancellationToken cancellationToken = default)
    {
        if (_blobServiceClient != null)
        {
            return _blobServiceClient;
        }

        using var activity = telemetry.ActivitySource.StartActivity("InitializeBlobServiceClient");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Get storage configuration from secrets
            _storageAccountName = await secretService.GetRequiredSecretAsync("DATA_STORAGE_ACCOUNT")
                                  ?? throw new InvalidOperationException("DATA_STORAGE_ACCOUNT not found in secrets or environment");

            _containerName = await secretService.GetRequiredSecretAsync("DATA_CONTAINER")
                             ?? throw new InvalidOperationException("DATA_CONTAINER not found in secrets or environment");

            _isUsingAzurite = await IsUsingAzuriteAsync(cancellationToken);

            if (_isUsingAzurite.Value)
            {
                // LOCAL DEVELOPMENT: Use connection string with account key
                LogInitializingAzurite(logger, _storageAccountName);

                var accountKey = await secretService.GetRequiredSecretAsync("DATA_STORAGE_ACCOUNT_KEY")
                                 ?? throw new InvalidOperationException("DATA_STORAGE_ACCOUNT_KEY not found for Azurite");

                var blobEndpoint = await secretService.GetRequiredSecretAsync("DATA_STORAGE_BLOB_ENDPOINT")
                                   ?? throw new InvalidOperationException("DATA_STORAGE_BLOB_ENDPOINT not found for Azurite");

                var credential = new StorageSharedKeyCredential(_storageAccountName, accountKey);
                _blobServiceClient = new BlobServiceClient(new Uri(blobEndpoint), credential);

                // Local Azurite volumes are often deleted during test resets. Bicep creates
                // the production container, but local development needs to bootstrap it.
                await _blobServiceClient
                    .GetBlobContainerClient(_containerName)
                    .CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            }
            else
            {
                // PRODUCTION: Use managed identity
                LogInitializingAzure(logger, _storageAccountName);

                var credential = new DefaultAzureCredential();
                var blobServiceUri = new Uri($"https://{_storageAccountName}.blob.core.windows.net");
                _blobServiceClient = new BlobServiceClient(blobServiceUri, credential);
            }

            stopwatch.Stop();
            activity?.SetTag("storage.account", _storageAccountName);
            activity?.SetTag("storage.is_azurite", _isUsingAzurite.Value);
            activity?.SetTag(ActivityAttributes.OperationStatus, "success");

            LogBlobServiceClientInitialized(logger, _storageAccountName, _isUsingAzurite.Value);

            return _blobServiceClient;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(ActivityAttributes.OperationStatus, "error");
            activity?.AddException(ex);

            LogBlobServiceClientInitializationFailed(logger, ex);
            throw;
        }
    }

    public async Task<BlobContainerClient> GetContainerClientAsync(CancellationToken cancellationToken = default)
    {
        var blobServiceClient = await GetBlobServiceClientAsync(cancellationToken);
        var containerName = await GetContainerNameAsync(cancellationToken);
        return blobServiceClient.GetBlobContainerClient(containerName);
    }

    public async Task<DataLakeServiceClient> GetDataLakeServiceClientAsync(CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetDataLakeServiceClient");

        var storageAccountName = await GetStorageAccountNameAsync(cancellationToken);
        var isAzurite = await IsUsingAzuriteAsync(cancellationToken);

        if (isAzurite)
        {
            throw new NotSupportedException("DataLake operations are not supported on Azurite");
        }

        var credential = new DefaultAzureCredential();
        var dataLakeServiceUri = new Uri($"https://{storageAccountName}.dfs.core.windows.net");

        return new DataLakeServiceClient(dataLakeServiceUri, credential);
    }

    public async Task<UserDelegationKey> GetUserDelegationKeyAsync(DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var blobServiceClient = await GetBlobServiceClientAsync(cancellationToken);

        // Check if we have a cached key that's still valid
        if (_cachedDelegationKey != null && _delegationKeyExpiresAt > expiresAt.AddMinutes(5))
        {
            return _cachedDelegationKey;
        }

        using var activity = telemetry.ActivitySource.StartActivity("GetUserDelegationKey");

        try
        {
            var keyExpiry = DateTimeOffset.UtcNow.AddHours(2);
            _cachedDelegationKey = await blobServiceClient.GetUserDelegationKeyAsync(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                keyExpiry,
                cancellationToken);

            _delegationKeyExpiresAt = keyExpiry;

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag("delegation_key.expires_at", keyExpiry);

            LogDelegationKeyRetrieved(logger, keyExpiry);

            return _cachedDelegationKey;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);

            LogDelegationKeyRetrievalFailed(logger, ex);
            throw;
        }
    }

    public async Task<DataLakeUserDelegationKey> GetDataLakeUserDelegationKeyAsync(DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        if (_cachedDataLakeDelegationKey != null && _dataLakeDelegationKeyExpiresAt > expiresAt.AddMinutes(5))
        {
            return _cachedDataLakeDelegationKey;
        }

        using var activity = telemetry.ActivitySource.StartActivity("GetDataLakeUserDelegationKey");

        try
        {
            var dataLakeServiceClient = await GetDataLakeServiceClientAsync(cancellationToken);
            var keyExpiry = DateTimeOffset.UtcNow.AddHours(2);
            _cachedDataLakeDelegationKey = await dataLakeServiceClient.GetUserDelegationKeyAsync(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                keyExpiry,
                cancellationToken);

            _dataLakeDelegationKeyExpiresAt = keyExpiry;

            activity?.SetTag(ActivityAttributes.OperationStatus, "success");
            activity?.SetTag("delegation_key.expires_at", keyExpiry);

            LogDelegationKeyRetrieved(logger, keyExpiry);

            return _cachedDataLakeDelegationKey;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);

            LogDelegationKeyRetrievalFailed(logger, ex);
            throw;
        }
    }

    public async Task<string> GetStorageAccountNameAsync(CancellationToken cancellationToken = default)
    {
        if (_storageAccountName != null)
        {
            return _storageAccountName;
        }

        // Initialize by getting the client (which loads config)
        await GetBlobServiceClientAsync(cancellationToken);
        return _storageAccountName!;
    }

    public async Task<string> GetContainerNameAsync(CancellationToken cancellationToken = default)
    {
        if (_containerName != null)
        {
            return _containerName;
        }

        // Initialize by getting the client (which loads config)
        await GetBlobServiceClientAsync(cancellationToken);
        return _containerName!;
    }

    public async Task<bool> IsUsingAzuriteAsync(CancellationToken cancellationToken = default)
    {
        if (_isUsingAzurite.HasValue)
        {
            return _isUsingAzurite.Value;
        }

        var useAzurite = await secretService.GetRequiredSecretAsync("USE_AZURITE");
        _isUsingAzurite = bool.TryParse(useAzurite, out var result) && result;
        return _isUsingAzurite.Value;
    }

    public async Task MoveBlobAsync(
        string sourceBlobName,
        string destinationBlobName,
        Dictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var containerClient = await GetContainerClientAsync(cancellationToken);
        var sourceBlobClient = containerClient.GetBlobClient(sourceBlobName);
        var destinationBlobClient = containerClient.GetBlobClient(destinationBlobName);

        // Check if source exists
        if (!await sourceBlobClient.ExistsAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Source blob not found: {sourceBlobName}");
        }

        // For ADLS Gen2 (HNS ON), use rename operation in Azure to avoid early deletion fees.
        // Copy+delete is used automatically only for Azurite/local development.
        var isAzurite = await IsUsingAzuriteAsync(cancellationToken);

        if (!isAzurite)
        {
            // Try ADLS Gen2 rename API (DataLakeFileClient)
            try
            {
                var dataLakeServiceClient = await GetDataLakeServiceClientAsync(cancellationToken);
                var fileSystemClient = dataLakeServiceClient.GetFileSystemClient(await GetContainerNameAsync(cancellationToken));
                var sourceFileClient = fileSystemClient.GetFileClient(sourceBlobName);

                // ADLS rename fails with RenameDestinationParentPathNotFound if the
                // destination's parent directory does not exist yet.
                var parentSeparator = destinationBlobName.LastIndexOf('/');
                if (parentSeparator > 0)
                {
                    await fileSystemClient
                        .GetDirectoryClient(destinationBlobName[..parentSeparator])
                        .CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                }

                await sourceFileClient.RenameAsync(destinationBlobName, cancellationToken: cancellationToken);

                // Set blob index tags if provided (after rename)
                if (tags != null && tags.Count > 0)
                {
                    var destBlobClient = containerClient.GetBlobClient(destinationBlobName);
                    await destBlobClient.SetTagsAsync(tags, cancellationToken: cancellationToken);
                }

                return;
            }
            catch (Exception ex)
            {
                if (!await IsCopyDeleteFallbackAllowedAsync(cancellationToken))
                {
                    LogAdlsRenameFailedCopyDeleteDisabled(logger, sourceBlobName, destinationBlobName, ex);
                    throw new InvalidOperationException(
                        "ADLS Gen2 rename failed and copy/delete fallback is disabled. " +
                        $"Source: '{sourceBlobName}', destination: '{destinationBlobName}'. " +
                        $"Set '{AllowCopyDeleteFallbackSecretName}=true' only if the extra cost and partial-failure risk are accepted.",
                        ex);
                }

                LogAdlsRenameFailedCopyDeleteEnabled(logger, sourceBlobName, destinationBlobName, ex);
            }
        }

        // Fallback: Copy + Delete
        var copyOperation = await destinationBlobClient.StartCopyFromUriAsync(
            sourceBlobClient.Uri,
            new BlobCopyFromUriOptions
            {
                DestinationConditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            },
            cancellationToken);

        await copyOperation.WaitForCompletionAsync(cancellationToken);

        await sourceBlobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);

        // Set blob index tags if provided
        if (tags != null && tags.Count > 0)
        {
            await destinationBlobClient.SetTagsAsync(tags, cancellationToken: cancellationToken);
        }
    }

    private async Task<bool> IsCopyDeleteFallbackAllowedAsync(CancellationToken cancellationToken)
    {
        var configuredValue = await secretService.GetSecretAsync(AllowCopyDeleteFallbackSecretName);

        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            configuredValue = await secretService.GetSecretAsync(AllowCopyDeleteFallbackConfigurationKey);
        }

        return bool.TryParse(configuredValue, out var allowFallback) && allowFallback;
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Initializing blob service client for Azurite (local dev) - Account: {storageAccount}")]
    static partial void LogInitializingAzurite(ILogger logger, string storageAccount);

    [LoggerMessage(LogLevel.Information, "Initializing blob service client for Azure (production) - Account: {storageAccount}")]
    static partial void LogInitializingAzure(ILogger logger, string storageAccount);

    [LoggerMessage(LogLevel.Information, "Blob service client initialized successfully - Account: {storageAccount}, Azurite: {isAzurite}")]
    static partial void LogBlobServiceClientInitialized(ILogger logger, string storageAccount, bool isAzurite);

    [LoggerMessage(LogLevel.Error, "Failed to initialize blob service client")]
    static partial void LogBlobServiceClientInitializationFailed(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Debug, "Retrieved user delegation key, expires at: {expiresAt}")]
    static partial void LogDelegationKeyRetrieved(ILogger logger, DateTimeOffset expiresAt);

    [LoggerMessage(LogLevel.Error, "Failed to retrieve user delegation key")]
    static partial void LogDelegationKeyRetrievalFailed(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Critical,
        "ADLS Gen2 rename failed for blob move {sourceBlobName} -> {destinationBlobName}. Copy/delete fallback is disabled; failing the move to avoid early deletion fees and partial-failure risk.")]
    static partial void LogAdlsRenameFailedCopyDeleteDisabled(ILogger logger, string sourceBlobName, string destinationBlobName, Exception ex);

    [LoggerMessage(LogLevel.Error,
        "ADLS Gen2 rename failed for blob move {sourceBlobName} -> {destinationBlobName}. Copy/delete fallback is explicitly enabled; continuing with copy/delete.")]
    static partial void LogAdlsRenameFailedCopyDeleteEnabled(ILogger logger, string sourceBlobName, string destinationBlobName, Exception ex);

    #endregion
}
