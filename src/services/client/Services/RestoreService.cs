using System.Security.Cryptography;
using Azure.Storage.Blobs;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Services;

public interface IRestoreService
{
    Task<bool> RestoreAsync(CancellationToken cancellationToken = default);
}

public partial class RestoreService(
    IBackupApiClient backupApiClient,
    IBackupStateService backupStateService,
    IBackupEncryptionService encryptionService,
    IOptions<BackupClientOptions> options,
    ILogger<RestoreService> logger,
    IRestoreBlobClientFactory? blobClients = null) : IRestoreService
{
    private readonly BackupClientOptions _options = options.Value;

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var restoreOptions = _options.Restore;
        var destinationRoot = restoreOptions.DestinationPath;
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new InvalidOperationException("BackupClient:Restore:DestinationPath must be configured for restore mode.");
        }

        var deviceId = restoreOptions.DeviceId ?? (await backupStateService.GetOrCreateDeviceStateAsync(cancellationToken)).DeviceId;
        var logicalPaths = restoreOptions.LogicalPaths;
        if (logicalPaths.Length == 0)
        {
            logicalPaths = await ListAllRestoreLogicalPathsAsync(deviceId, restoreOptions.ListPageSize, cancellationToken);
        }

        if (logicalPaths.Length == 0)
        {
            LogNoFilesToRestore(logger, deviceId);
            return true;
        }

        var request = new StartRestoreRequest
        {
            LogicalPaths = logicalPaths,
            RehydrateArchivedFiles = restoreOptions.RehydrateArchivedFiles
        };
        var restore = await backupApiClient.StartRestore(deviceId, request, cancellationToken);
        var selected = restore.Files.ToArray();
        var waitStarted = System.Diagnostics.Stopwatch.StartNew();
        while (!restore.IsReady)
        {
            var waiting = restore.Files.Count(file => file.Availability != RestoreFileAvailability.Ready);
            logger.LogInformation("Restore is waiting for {WaitingFiles} of {TotalFiles} archived files", waiting, selected.Length);
            if (!restoreOptions.RehydrateArchivedFiles)
                throw new InvalidOperationException("Selected files are archived. Enable Restore:RehydrateArchivedFiles to prepare them automatically.");
            if (waitStarted.Elapsed >= TimeSpan.FromMinutes(Math.Max(0, restoreOptions.RehydrationTimeoutMinutes)))
                throw new TimeoutException("Restore preparation timed out. Rehydration continues in storage; retry restore later.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, restoreOptions.RehydrationPollIntervalSeconds)), cancellationToken);
            restore = await backupApiClient.StartRestore(deviceId, request, cancellationToken);
            if (restore.Files.Count != selected.Length || selected.Any(file => !restore.Files.Any(current =>
                    current.LogicalPath == file.LogicalPath && current.UniqueFileId == file.UniqueFileId &&
                    current.Sha256 == file.Sha256 && current.Size == file.Size)))
                throw new InvalidOperationException("Selected backup versions changed while preparing restore. Restart restore to select consistent versions.");
        }

        var sas = restore.SasUrlInfo;
        var containerClient = CreateContainer(sas.Url);

        foreach (var file in selected)
        {
            if (sas.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                var renewed = await backupApiClient.RefreshRestoreSas(deviceId, cancellationToken);
                if (renewed.DeviceId != deviceId) throw new InvalidOperationException("Restore credentials belong to a different device.");
                sas = renewed.SasUrlInfo;
                containerClient = CreateContainer(sas.Url);
            }
            try
            {
                await RestoreFileAsync(containerClient, sas.BasePath, sas.IsPathEmbedded, destinationRoot, file, restoreOptions.OverwriteExisting, cancellationToken);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 403 && ex.ErrorCode == "AuthenticationFailed")
            {
                var renewed = await backupApiClient.RefreshRestoreSas(deviceId, cancellationToken);
                if (renewed.DeviceId != deviceId) throw new InvalidOperationException("Restore credentials belong to a different device.");
                sas = renewed.SasUrlInfo;
                containerClient = CreateContainer(sas.Url);
                await RestoreFileAsync(containerClient, sas.BasePath, sas.IsPathEmbedded, destinationRoot, file, restoreOptions.OverwriteExisting, cancellationToken);
            }
        }

        LogRestoreCompleted(logger, restore.Files.Count, destinationRoot);
        return true;
    }

    private BlobContainerClient CreateContainer(Uri url)
        => (blobClients ?? new RestoreBlobClientFactory()).Create(TranslateStorageUrlForLocalDevelopment(url));

    private async Task<string[]> ListAllRestoreLogicalPathsAsync(Guid deviceId, int pageSize, CancellationToken cancellationToken)
    {
        var logicalPaths = new List<string>();
        string? continuationToken = null;

        do
        {
            var page = await backupApiClient.ListRestoreFiles(deviceId, pageSize, continuationToken, cancellationToken);
            logicalPaths.AddRange(page.Files.Select(f => f.LogicalPath));
            continuationToken = page.NextContinuationToken;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));

        return logicalPaths.ToArray();
    }

    private async Task RestoreFileAsync(
        BlobContainerClient containerClient,
        string? basePath,
        bool isPathEmbedded,
        string destinationRoot,
        RestoreFileItem file,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        var blobName = isPathEmbedded || string.IsNullOrWhiteSpace(basePath)
            ? file.UniqueFileId
            : $"{basePath.TrimEnd('/')}/{file.UniqueFileId}";

        var destinationPath = GetSafeDestinationPath(destinationRoot, file.LogicalPath);
        if (File.Exists(destinationPath) && !overwriteExisting)
        {
            throw new IOException($"Restore destination already exists: {destinationPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationRoot);
        var tempDownloadPath = Path.Combine(Path.GetTempPath(), $"backup-restore-{Guid.NewGuid():N}.bin");
        var tempPlaintextPath = Path.Combine(Path.GetTempPath(), $"backup-restore-plain-{Guid.NewGuid():N}.bin");

        try
        {
            var lastReported = -10;
            var progress = new Progress<long>(bytes =>
            {
                var percent = file.Size == 0 ? 100 : (int)Math.Min(100, bytes * 100.0 / file.Size);
                if (percent >= lastReported + 10)
                {
                    lastReported = percent;
                    logger.LogInformation("Restoring {LogicalPath}: {Percent}% ({Bytes}/{TotalBytes} bytes)", file.LogicalPath, percent, bytes, file.Size);
                }
            });
            await containerClient.GetBlobClient(blobName).DownloadToAsync(tempDownloadPath,
                new Azure.Storage.Blobs.Models.BlobDownloadToOptions { ProgressHandler = progress }, cancellationToken);
            await VerifyDownloadedFileAsync(tempDownloadPath, file, cancellationToken);

            if (file.Encryption == null)
            {
                MoveRestoredFile(tempDownloadPath, destinationPath, overwriteExisting);
            }
            else
            {
                await encryptionService.DecryptFileAsync(tempDownloadPath, tempPlaintextPath, file.Encryption, cancellationToken);
                MoveRestoredFile(tempPlaintextPath, destinationPath, overwriteExisting);
            }

            File.SetLastWriteTimeUtc(destinationPath, file.LastWriteUtc.UtcDateTime);
            LogFileRestored(logger, file.LogicalPath, destinationPath);
        }
        finally
        {
            DeleteIfExists(tempDownloadPath);
            DeleteIfExists(tempPlaintextPath);
        }
    }

    private static async Task VerifyDownloadedFileAsync(string filePath, RestoreFileItem file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(filePath);
        if (info.Length != file.Size)
        {
            throw new InvalidOperationException($"Downloaded file size mismatch for '{file.LogicalPath}'.");
        }

        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Downloaded file SHA-256 mismatch for '{file.LogicalPath}'.");
        }
    }

    private static string GetSafeDestinationPath(string destinationRoot, string logicalPath)
    {
        var normalizedRelativePath = logicalPath.Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(destinationRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalizedRelativePath));

        if (!fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Restore path escapes destination root: {logicalPath}");
        }

        return fullPath;
    }

    private static void MoveRestoredFile(string sourcePath, string destinationPath, bool overwriteExisting)
    {
        if (overwriteExisting && File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(sourcePath, destinationPath);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static Uri TranslateStorageUrlForLocalDevelopment(Uri originalUrl)
    {
        if (originalUrl.Host is "azurite" or "storage")
        {
            var builder = new UriBuilder(originalUrl) { Host = "localhost" };
            return builder.Uri;
        }

        return originalUrl;
    }

    [LoggerMessage(LogLevel.Information, "No files available to restore for device {deviceId}")]
    static partial void LogNoFilesToRestore(ILogger logger, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Restored {fileCount} files to {destinationRoot}")]
    static partial void LogRestoreCompleted(ILogger logger, int fileCount, string destinationRoot);

    [LoggerMessage(LogLevel.Information, "Restored {logicalPath} to {destinationPath}")]
    static partial void LogFileRestored(ILogger logger, string logicalPath, string destinationPath);
}
