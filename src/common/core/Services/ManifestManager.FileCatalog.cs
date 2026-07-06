using System.Diagnostics;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Logging.OpenTelemetry;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager
{
    public async Task<FileEntry?> GetFileEntryAsync(Guid deviceId, string relativePath, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetFileEntry");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag("relative_path", relativePath);

        var document = await store.GetAsync<FileEntry>(
            FileEntryDocument, DevicePartition(deviceId), EncodeStateKeySegment(relativePath), cancellationToken);

        if (document != null)
        {
            var fileEntry = document.Data;
            fileEntry.ETag = document.ETag;
            activity?.SetTag(ActivityAttributes.StateETag, document.ETag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileentry" }, { "result", "found" } });
            return fileEntry;
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileentry" }, { "result", "not_found" } });
        return null;
    }

    public async Task SaveFileEntryAsync(FileEntry fileEntry, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("SaveFileEntry");
        activity?.SetTag(ActivityAttributes.DeviceId, fileEntry.DeviceId);
        activity?.SetTag("relative_path", fileEntry.RelativePath);

        var expectedETag = fileEntry.ETag;

        try
        {
            fileEntry.ETag = await store.UpsertAsync(
                FileEntryDocument,
                DevicePartition(fileEntry.DeviceId),
                EncodeStateKeySegment(fileEntry.RelativePath),
                fileEntry,
                expectedETag,
                sortKey: fileEntry.RelativePath.ToLowerInvariant(),
                cancellationToken: cancellationToken);
        }
        catch (StateConcurrencyException)
        {
            LogConcurrentFileEntryUpdate(logger, fileEntry.RelativePath, fileEntry.DeviceId, expectedETag);
            throw new InvalidOperationException($"Concurrent update detected for FileEntry {fileEntry.RelativePath}");
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "fileentry" } });
        LogFileEntrySaved(logger, fileEntry.RelativePath, fileEntry.DeviceId);
    }

    public async Task<FileVersion?> GetFileVersionAsync(Guid deviceId, string uniqueFileId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetFileVersion");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag("unique_file_id", uniqueFileId);

        var document = await store.GetAsync<FileVersion>(
            FileVersionDocument, DevicePartition(deviceId), EncodeStateKeySegment(uniqueFileId), cancellationToken);

        if (document != null)
        {
            var fileVersion = document.Data;
            fileVersion.ETag = document.ETag;
            activity?.SetTag(ActivityAttributes.StateETag, document.ETag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileversion" }, { "result", "found" } });
            return fileVersion;
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "entity", "fileversion" }, { "result", "not_found" } });
        return null;
    }

    public async Task SaveFileVersionAsync(FileVersion fileVersion, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("SaveFileVersion");
        activity?.SetTag(ActivityAttributes.DeviceId, fileVersion.DeviceId);
        activity?.SetTag("unique_file_id", fileVersion.UniqueFileId);

        var expectedETag = fileVersion.ETag;

        try
        {
            fileVersion.ETag = await store.UpsertAsync(
                FileVersionDocument,
                DevicePartition(fileVersion.DeviceId),
                EncodeStateKeySegment(fileVersion.UniqueFileId),
                fileVersion,
                expectedETag,
                cancellationToken: cancellationToken);
        }
        catch (StateConcurrencyException)
        {
            LogConcurrentFileVersionUpdate(logger, fileVersion.UniqueFileId, fileVersion.DeviceId, expectedETag);
            throw new InvalidOperationException($"Concurrent update detected for FileVersion {fileVersion.UniqueFileId}");
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

        var page = await store.QueryAsync<FileEntry>(new DocumentQuery
        {
            Type = FileEntryDocument,
            PartitionKey = DevicePartition(deviceId),
            Order = DocumentOrder.SortKeyAscending,
            PageSize = pageSize,
            ContinuationToken = continuationToken
        }, cancellationToken);

        var entries = new List<FileEntry>(page.Items.Count);
        foreach (var document in page.Items)
        {
            var entry = document.Data;
            entry.ETag = document.ETag;
            entries.Add(entry);
        }

        LogFileEntriesQueried(logger, deviceId);
        return new FileEntryPage
        {
            Entries = entries,
            PageSize = pageSize,
            ContinuationToken = continuationToken,
            NextContinuationToken = page.NextContinuationToken
        };
    }
}
