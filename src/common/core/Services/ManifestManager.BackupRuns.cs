using System.Diagnostics;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Logging.OpenTelemetry;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager
{
    public async Task<BackupRun> CreateBackupRunAsync(Guid deviceId, Guid runId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("CreateBackupRun");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var state = new BackupRun
        {
            RunId = runId,
            DeviceId = deviceId,
            StartedAt = startedAt,
            Status = BackupRunStatus.Queued,
        };

        state.ETag = await SaveBackupRunAsync(state, etag: null, cancellationToken);

        telemetry.StateOperations.Add(1, new TagList { { "operation", "create" }, { "store", "manifest" } });

        LogBackupRunCreated(logger, runId, deviceId);
        return state;
    }

    public async Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetBackupRun");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var document = await store.GetAsync<BackupRun>(
            BackupRunDocument, DevicePartition(deviceId), $"{runId:N}", cancellationToken);

        if (document != null)
        {
            var run = document.Data;
            run.ETag = document.ETag;
            activity?.SetTag(ActivityAttributes.StateETag, document.ETag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "result", "found" } });
            return run;
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "result", "not_found" } });
        throw new BackupRunNotFoundException(deviceId, runId);
    }

    public async Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await GetBackupRunAsync(deviceId, runId, cancellationToken);

        if (run.Status == BackupRunStatus.Succeeded)
        {
            throw new BackupRunAlreadyCommittedException(deviceId, runId);
        }

        if (run.Status == BackupRunStatus.Failed)
        {
            throw new InvalidBackupRunStateException(deviceId, runId, run.Status, BackupRunStatus.Queued);
        }

        var expectedETag = run.ETag;
        run.Status = BackupRunStatus.Succeeded;
        run.CompletedAt = DateTimeOffset.UtcNow;

        try
        {
            run.ETag = await SaveBackupRunAsync(run, expectedETag, cancellationToken);
        }
        catch (StateConcurrencyException)
        {
            LogConcurrentUpdateDetected(logger, runId, deviceId, expectedETag);
            throw new ConcurrentUpdateException(deviceId, runId, expectedETag, actualETag: null);
        }

        LogBackupRunCommitted(logger, runId, deviceId, run.Status);
        return run;
    }

    public async Task<BackupRun> UpdateBackupRunAsync(Guid deviceId, Guid runId, BackupRun updatedRun,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("UpdateBackupRun");
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var expectedETag = updatedRun.ETag;

        try
        {
            updatedRun.ETag = await SaveBackupRunAsync(updatedRun, expectedETag, cancellationToken);
        }
        catch (StateConcurrencyException)
        {
            LogConcurrentUpdateDetected(logger, runId, deviceId, expectedETag);
            throw new ConcurrentUpdateException(deviceId, runId, expectedETag, actualETag: null);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "update" }, { "store", "manifest" } });
        LogBackupRunUpdated(logger, runId, deviceId, updatedRun.Status);

        return updatedRun;
    }

    public async Task<BackupRunPage> GetBackupRunsPageAsync(BackupRunQuery query, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetBackupRunsPage");
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        activity?.SetTag("state.page_size", pageSize);

        var page = await store.QueryAsync<BackupRun>(new DocumentQuery
        {
            Type = BackupRunDocument,
            PartitionKey = query.DeviceId.HasValue ? DevicePartition(query.DeviceId.Value) : null,
            SortValueFrom = query.StartedFromUtc?.ToUnixTimeMilliseconds(),
            SortValueTo = query.StartedToUtc?.ToUnixTimeMilliseconds(),
            Order = DocumentOrder.SortValueDescending,
            PageSize = pageSize,
            ContinuationToken = query.ContinuationToken
        }, cancellationToken);

        var runs = new List<BackupRun>(page.Items.Count);
        foreach (var document in page.Items)
        {
            var run = document.Data;
            run.ETag = document.ETag;

            // The status filter is applied per page: filtered pages may contain fewer
            // than pageSize items while a continuation token is still present.
            if (!query.Status.HasValue || run.Status == query.Status.Value)
            {
                runs.Add(run);
            }
        }

        return new BackupRunPage
        {
            Runs = runs,
            PageSize = pageSize,
            ContinuationToken = query.ContinuationToken,
            NextContinuationToken = page.NextContinuationToken
        };
    }

    // Entries per manifest chunk document. With encrypted entries at roughly ~1KB each this keeps a
    // chunk well under the state store's per-document size limit (Cosmos ~2MB).
    private const int RunManifestChunkSize = 500;

    public Task SaveRunManifestAsync(Guid deviceId, Guid runId, RunManifest manifest, CancellationToken cancellationToken = default)
        => SaveRunManifestAsync(deviceId, runId, ToStream(manifest), cancellationToken);

    private static async IAsyncEnumerable<RunManifestStreamItem> ToStream(RunManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            yield return new RunManifestStreamItem(file, null);
        }

        foreach (var path in manifest.Deleted)
        {
            yield return new RunManifestStreamItem(null, path);
        }

        await Task.CompletedTask;
    }

    public async Task<(int FileCount, int DeletedCount)> SaveRunManifestAsync(
        Guid deviceId,
        Guid runId,
        IAsyncEnumerable<RunManifestStreamItem> items,
        CancellationToken cancellationToken = default)
    {
        // Split the manifest across small chunk documents so a run with many files never produces a
        // single document that exceeds the store's per-document size limit. Chunks are filled and
        // written as entries arrive, so a manifest with hundreds of thousands of entries is never held
        // in memory: at most one chunk's worth is buffered at a time. A small header document written
        // last records the totals for reassembly.
        var chunkIndex = 0;
        var fileCount = 0;
        var deletedCount = 0;
        var files = new List<ManifestFileEntry>(RunManifestChunkSize);
        var deleted = new List<string>(RunManifestChunkSize);

        await foreach (var item in items.WithCancellation(cancellationToken))
        {
            if (item.File is not null)
            {
                files.Add(item.File);
                fileCount++;

                if (files.Count == RunManifestChunkSize)
                {
                    await FlushFilesAsync();
                }
            }
            else if (item.DeletedPath is not null)
            {
                deleted.Add(item.DeletedPath);
                deletedCount++;

                if (deleted.Count == RunManifestChunkSize)
                {
                    await FlushDeletedAsync();
                }
            }
        }

        await FlushFilesAsync();
        await FlushDeletedAsync();

        // Write the header last so any reader that observes it can also read every chunk it references.
        var header = new RunManifestHeader
        {
            SchemaVersion = RunManifestHeader.ChunkedSchemaVersion,
            DeviceId = $"{deviceId:N}",
            RunId = $"{runId:N}",
            FileCount = fileCount,
            DeletedCount = deletedCount,
            ChunkCount = chunkIndex,
            ChunkSize = RunManifestChunkSize
        };

        await store.UpsertAsync(
            RunManifestDocument, DevicePartition(deviceId), $"{runId:N}", header,
            cancellationToken: cancellationToken);

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "runmanifest" } });

        return (fileCount, deletedCount);

        async Task FlushFilesAsync()
        {
            if (files.Count == 0)
            {
                return;
            }

            await SaveRunManifestChunkAsync(deviceId, runId, chunkIndex, new RunManifestChunk
            {
                DeviceId = $"{deviceId:N}",
                RunId = $"{runId:N}",
                Index = chunkIndex,
                Files = [.. files]
            }, cancellationToken);

            chunkIndex++;
            files.Clear();
        }

        async Task FlushDeletedAsync()
        {
            if (deleted.Count == 0)
            {
                return;
            }

            await SaveRunManifestChunkAsync(deviceId, runId, chunkIndex, new RunManifestChunk
            {
                DeviceId = $"{deviceId:N}",
                RunId = $"{runId:N}",
                Index = chunkIndex,
                Deleted = [.. deleted]
            }, cancellationToken);

            chunkIndex++;
            deleted.Clear();
        }
    }

    public async Task<RunManifestHeader?> GetRunManifestHeaderAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var document = await store.GetAsync<RunManifestHeader>(
            RunManifestDocument, DevicePartition(deviceId), $"{runId:N}", cancellationToken);

        telemetry.StateOperations.Add(1, new TagList
        {
            { "operation", "get" },
            { "store", "manifest" },
            { "entity", "runmanifestheader" },
            { "result", document == null ? "not_found" : "found" }
        });

        return document?.Data;
    }

    public async Task<RunManifestEntryPage> GetRunManifestEntryPageAsync(
        Guid deviceId,
        Guid runId,
        int pageSize,
        string? chunkToken = null,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be greater than zero.");
        }

        var header = await GetRunManifestHeaderAsync(deviceId, runId, cancellationToken);

        // Legacy (v1) manifests kept their entries inline on the header. Those documents had to fit
        // the store's per-document limit, so paging them in memory is bounded.
        if (header != null && header.SchemaVersion < RunManifestHeader.ChunkedSchemaVersion)
        {
            return PageInlineHeader(header, pageSize, skip);
        }

        var files = new List<ManifestFileEntry>();
        var deleted = new List<string>();

        // One chunk per query: a page can end mid-chunk, and the token that fetched that chunk is
        // what the next page needs in order to resume inside it.
        var currentToken = chunkToken;

        while (files.Count + deleted.Count < pageSize)
        {
            var page = await store.QueryAsync<RunManifestChunk>(new DocumentQuery
            {
                Type = RunManifestChunkDocument,
                PartitionKey = RunManifestPartition(deviceId, runId),
                Order = DocumentOrder.SortKeyAscending,
                PageSize = 1,
                ContinuationToken = currentToken
            }, cancellationToken);

            if (page.Items.Count == 0)
            {
                return Result(files, deleted, nextToken: null, nextSkip: 0, hasMore: false);
            }

            var chunk = page.Items[0].Data;
            var chunkTotal = chunk.Files.Count + chunk.Deleted.Count;
            var remaining = pageSize - (files.Count + deleted.Count);
            var taken = 0;

            // A chunk carries either files or deletions, never both, so a single running offset
            // walks whichever collection this chunk holds.
            foreach (var entry in chunk.Files.Skip(skip).Take(remaining))
            {
                files.Add(entry);
                taken++;
            }

            foreach (var path in chunk.Deleted.Skip(skip).Take(remaining))
            {
                deleted.Add(path);
                taken++;
            }

            if (skip + taken < chunkTotal)
            {
                // Stopped inside this chunk; resume here next time.
                return Result(files, deleted, currentToken, skip + taken, hasMore: true);
            }

            skip = 0;
            currentToken = page.NextContinuationToken;

            if (string.IsNullOrEmpty(currentToken))
            {
                return Result(files, deleted, nextToken: null, nextSkip: 0, hasMore: false);
            }
        }

        return Result(files, deleted, currentToken, skip, hasMore: !string.IsNullOrEmpty(currentToken));

        static RunManifestEntryPage Result(
            List<ManifestFileEntry> files,
            List<string> deleted,
            string? nextToken,
            int nextSkip,
            bool hasMore)
            => new()
            {
                Files = files,
                Deleted = deleted,
                NextChunkToken = nextToken,
                NextSkip = nextSkip,
                HasMore = hasMore
            };

        static RunManifestEntryPage PageInlineHeader(RunManifestHeader header, int pageSize, int skip)
        {
            var inlineFiles = header.Files ?? [];
            var inlineDeleted = header.Deleted ?? [];

            var files = inlineFiles.Skip(skip).Take(pageSize).ToList();
            var deleted = inlineDeleted
                .Skip(Math.Max(0, skip - inlineFiles.Count))
                .Take(pageSize - files.Count)
                .ToList();

            var consumed = skip + files.Count + deleted.Count;
            var total = inlineFiles.Count + inlineDeleted.Count;

            return new RunManifestEntryPage
            {
                Files = files,
                Deleted = deleted,
                NextChunkToken = null,
                NextSkip = consumed,
                HasMore = consumed < total
            };
        }
    }


    private Task<string> SaveRunManifestChunkAsync(Guid deviceId, Guid runId, int index, RunManifestChunk chunk,
        CancellationToken cancellationToken)
        => store.UpsertAsync(
            RunManifestChunkDocument,
            RunManifestPartition(deviceId, runId),
            $"{runId:N}:{index:D6}",
            chunk,
            sortKey: $"{index:D6}",
            cancellationToken: cancellationToken);

    private Task<string> SaveBackupRunAsync(BackupRun run, string? etag, CancellationToken cancellationToken)
        => store.UpsertAsync(
            BackupRunDocument,
            DevicePartition(run.DeviceId),
            $"{run.RunId:N}",
            run,
            etag,
            sortValue: run.StartedAt.ToUnixTimeMilliseconds(),
            cancellationToken: cancellationToken);
}
