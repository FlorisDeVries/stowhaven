using System.Diagnostics;
using FlorisDeV.BackupContracts.Constants;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Logging.OpenTelemetry;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager
{
    public async Task<FileEntry?> GetFileEntryAsync(Guid deviceId, string relativePath, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetFileEntry");
        var stateKey = GetFileEntryStateKey(deviceId, relativePath);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag("relative_path", relativePath);

        var (fileEntry, etag) = await daprClient.GetStateAndETagAsync<FileEntry>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (fileEntry != null)
        {
            fileEntry.ETag = etag;
            activity?.SetTag(ActivityAttributes.StateETag, etag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileentry" }, { "result", "found" } });
        }
        else
        {
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileentry" }, { "result", "not_found" } });
        }

        return fileEntry;
    }

    public async Task SaveFileEntryAsync(FileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("SaveFileEntry");
        var stateKey = GetFileEntryStateKey(fileEntry.DeviceId, fileEntry.RelativePath);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, fileEntry.DeviceId);
        activity?.SetTag("relative_path", fileEntry.RelativePath);

        if (!string.IsNullOrEmpty(fileEntry.ETag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                fileEntry,
                fileEntry.ETag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                LogConcurrentFileEntryUpdate(logger, fileEntry.RelativePath, fileEntry.DeviceId, fileEntry.ETag);
                throw new InvalidOperationException($"Concurrent update detected for FileEntry {fileEntry.RelativePath}");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                fileEntry,
                cancellationToken: cancellationToken);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "fileentry" } });
        await AddFileEntryToIndexAsync(fileEntry.DeviceId, fileEntry.RelativePath, cancellationToken);
        LogFileEntrySaved(logger, fileEntry.RelativePath, fileEntry.DeviceId);
    }

    public async Task<FileVersion?> GetFileVersionAsync(Guid deviceId, string uniqueFileId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetFileVersion");
        var stateKey = GetFileVersionStateKey(deviceId, uniqueFileId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag("unique_file_id", uniqueFileId);

        var (fileVersion, etag) = await daprClient.GetStateAndETagAsync<FileVersion>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (fileVersion != null)
        {
            fileVersion.ETag = etag;
            activity?.SetTag(ActivityAttributes.StateETag, etag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileversion" }, { "result", "found" } });
        }
        else
        {
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileversion" }, { "result", "not_found" } });
        }

        return fileVersion;
    }

    public async Task SaveFileVersionAsync(FileVersion fileVersion, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("SaveFileVersion");
        var stateKey = GetFileVersionStateKey(fileVersion.DeviceId, fileVersion.UniqueFileId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, fileVersion.DeviceId);
        activity?.SetTag("unique_file_id", fileVersion.UniqueFileId);

        if (!string.IsNullOrEmpty(fileVersion.ETag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                fileVersion,
                fileVersion.ETag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                LogConcurrentFileVersionUpdate(logger, fileVersion.UniqueFileId, fileVersion.DeviceId, fileVersion.ETag);
                throw new InvalidOperationException($"Concurrent update detected for FileVersion {fileVersion.UniqueFileId}");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                fileVersion,
                cancellationToken: cancellationToken);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "fileversion" } });
        LogFileVersionSaved(logger, fileVersion.UniqueFileId, fileVersion.DeviceId);
    }

    public async Task<List<FileEntry>> GetAllFileEntriesAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var entries = new List<FileEntry>();
        string? continuationToken = null;

        do
        {
            var page = await GetFileEntriesPageAsync(deviceId, pageSize: 500, continuationToken, cancellationToken);
            entries.AddRange(page.Entries);
            continuationToken = page.NextContinuationToken;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));

        return entries;
    }

    public async Task<FileEntryPage> GetFileEntriesPageAsync(Guid deviceId, int pageSize, string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetAllFileEntries");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag("state.page_size", pageSize);

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than zero.");
        }

        var indexKey = GetFileEntryIndexKey(deviceId);
        var index = await daprClient.GetStateAsync<FileEntryIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        if (index == null || index.RelativePaths.Count == 0)
        {
            LogFileEntriesQueried(logger, deviceId);
            return new FileEntryPage
            {
                Entries = [],
                PageSize = pageSize,
                ContinuationToken = continuationToken,
                NextContinuationToken = null
            };
        }

        var offset = DecodeContinuationToken(continuationToken);
        if (offset >= index.RelativePaths.Count)
        {
            LogFileEntriesQueried(logger, deviceId);
            return new FileEntryPage
            {
                Entries = [],
                PageSize = pageSize,
                ContinuationToken = continuationToken,
                NextContinuationToken = null
            };
        }

        var relativePaths = index.RelativePaths
            .Order(StringComparer.OrdinalIgnoreCase)
            .Skip(offset)
            .Take(pageSize)
            .ToArray();

        var entries = new List<FileEntry>(relativePaths.Length);
        foreach (var relativePath in relativePaths)
        {
            var entry = await GetFileEntryAsync(deviceId, relativePath, cancellationToken);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        var nextOffset = offset + relativePaths.Length;
        LogFileEntriesQueried(logger, deviceId);
        return new FileEntryPage
        {
            Entries = entries,
            PageSize = pageSize,
            ContinuationToken = continuationToken,
            NextContinuationToken = nextOffset < index.RelativePaths.Count ? EncodeContinuationToken(nextOffset) : null
        };
    }
}
