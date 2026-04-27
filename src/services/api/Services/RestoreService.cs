using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupApi.Services;

public interface IRestoreService
{
    Task<ListRestoreFilesResponse> ListRestoreFilesAsync(Guid deviceId, int pageSize = RestoreService.DefaultPageSize,
        string? continuationToken = null, CancellationToken cancellationToken = default);

    Task<StartRestoreResponse> StartRestoreAsync(
        Guid deviceId,
        StartRestoreRequest request,
        string? clientIp = null,
        CancellationToken cancellationToken = default);
}

public class RestoreService(
    IManifestManager manifestManager,
    ISasUrlService sasUrlService) : IRestoreService
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 1000;

    public async Task<ListRestoreFilesResponse> ListRestoreFilesAsync(Guid deviceId, int pageSize = DefaultPageSize,
        string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        pageSize = NormalizePageSize(pageSize);
        var page = await manifestManager.GetFileEntriesPageAsync(deviceId, pageSize, continuationToken, cancellationToken);
        var files = new List<RestoreFileItem>();

        foreach (var entry in page.Entries.Where(e => !e.IsDeleted).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var version = await manifestManager.GetFileVersionAsync(deviceId, entry.CurrentVersionId, cancellationToken);
            if (version is not { State: FileVersionState.Active })
            {
                continue;
            }

            files.Add(ToRestoreFileItem(entry, version));
        }

        return new ListRestoreFilesResponse
        {
            DeviceId = deviceId,
            Files = files,
            PageSize = page.PageSize,
            ContinuationToken = page.ContinuationToken,
            NextContinuationToken = page.NextContinuationToken
        };
    }

    public async Task<StartRestoreResponse> StartRestoreAsync(
        Guid deviceId,
        StartRestoreRequest request,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        if (request.LogicalPaths.Count == 0)
        {
            throw new InvalidOperationException("At least one logical path must be selected for restore.");
        }

        var files = new List<RestoreFileItem>(request.LogicalPaths.Count);
        foreach (var logicalPath in request.LogicalPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = await manifestManager.GetFileEntryAsync(deviceId, logicalPath, cancellationToken);
            if (entry == null || entry.IsDeleted)
            {
                throw new FileNotFoundException($"Restore file '{logicalPath}' was not found for device {deviceId}.");
            }

            var version = await manifestManager.GetFileVersionAsync(deviceId, entry.CurrentVersionId, cancellationToken);
            if (version is not { State: FileVersionState.Active })
            {
                throw new FileNotFoundException($"Active restore version '{entry.CurrentVersionId}' was not found for '{logicalPath}'.");
            }

            files.Add(ToRestoreFileItem(entry, version));
        }

        var restorePath = $"devices/{deviceId:N}/files";
        var sasUrl = await sasUrlService.GenerateReadSasUrlAsync(restorePath, clientIp, ttlMinutes: 60, cancellationToken);

        return new StartRestoreResponse
        {
            RestoreId = Guid.NewGuid(),
            DeviceId = deviceId,
            ExpiresAt = sasUrl.ExpiresAt,
            SasUrlInfo = sasUrl,
            Files = files
        };
    }

    private static RestoreFileItem ToRestoreFileItem(FileEntry entry, FileVersion version) => new()
    {
        LogicalPath = entry.RelativePath,
        UniqueFileId = version.UniqueFileId,
        Sha256 = version.Sha256,
        Size = version.Size,
        LastWriteUtc = entry.LastWriteUtc,
        Encryption = version.Encryption
    };

    private static int NormalizePageSize(int pageSize)
    {
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than zero.");
        }

        return Math.Min(pageSize, MaxPageSize);
    }
}
