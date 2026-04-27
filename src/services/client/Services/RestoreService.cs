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
    ILogger<RestoreService> logger) : IRestoreService
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

        var restore = await backupApiClient.StartRestore(deviceId, new StartRestoreRequest
        {
            LogicalPaths = logicalPaths
        }, cancellationToken);

        var sasUrl = TranslateStorageUrlForLocalDevelopment(restore.SasUrlInfo.Url);
        var containerClient = new BlobContainerClient(sasUrl);

        foreach (var file in restore.Files)
        {
            await RestoreFileAsync(containerClient, restore.SasUrlInfo.BasePath, restore.SasUrlInfo.IsPathEmbedded, destinationRoot, file, restoreOptions.OverwriteExisting, cancellationToken);
        }

        LogRestoreCompleted(logger, restore.Files.Count, destinationRoot);
        return true;
    }

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
            await containerClient.GetBlobClient(blobName).DownloadToAsync(tempDownloadPath, cancellationToken);
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
