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

    public async Task SaveRunManifestAsync(Guid deviceId, Guid runId, RunManifest manifest, CancellationToken cancellationToken = default)
    {
        // Split the manifest across small chunk documents so a run with many files never produces a
        // single document that exceeds the store's per-document size limit. Files come first in the
        // chunk sequence, then deletions; a small header document records the totals for reassembly.
        var chunkIndex = 0;

        for (var offset = 0; offset < manifest.Files.Count; offset += RunManifestChunkSize)
        {
            var slice = manifest.Files.GetRange(offset, Math.Min(RunManifestChunkSize, manifest.Files.Count - offset));
            await SaveRunManifestChunkAsync(deviceId, runId, chunkIndex++, new RunManifestChunk
            {
                DeviceId = $"{deviceId:N}",
                RunId = $"{runId:N}",
                Index = chunkIndex - 1,
                Files = slice
            }, cancellationToken);
        }

        for (var offset = 0; offset < manifest.Deleted.Count; offset += RunManifestChunkSize)
        {
            var slice = manifest.Deleted.GetRange(offset, Math.Min(RunManifestChunkSize, manifest.Deleted.Count - offset));
            await SaveRunManifestChunkAsync(deviceId, runId, chunkIndex++, new RunManifestChunk
            {
                DeviceId = $"{deviceId:N}",
                RunId = $"{runId:N}",
                Index = chunkIndex - 1,
                Deleted = slice
            }, cancellationToken);
        }

        // Write the header last so any reader that observes it can also read every chunk it references.
        var header = new RunManifestHeader
        {
            SchemaVersion = RunManifestHeader.ChunkedSchemaVersion,
            DeviceId = manifest.DeviceId,
            RunId = manifest.RunId,
            FileCount = manifest.Files.Count,
            DeletedCount = manifest.Deleted.Count,
            ChunkCount = chunkIndex,
            ChunkSize = RunManifestChunkSize
        };

        await store.UpsertAsync(
            RunManifestDocument, DevicePartition(deviceId), $"{runId:N}", header,
            cancellationToken: cancellationToken);

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "runmanifest" } });
    }

    public async Task<RunManifest?> GetRunManifestAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var document = await store.GetAsync<RunManifestHeader>(
            RunManifestDocument, DevicePartition(deviceId), $"{runId:N}", cancellationToken);

        telemetry.StateOperations.Add(1, new TagList
        {
            { "operation", "get" },
            { "store", "manifest" },
            { "entity", "runmanifest" },
            { "result", document == null ? "not_found" : "found" }
        });

        if (document == null)
        {
            return null;
        }

        var header = document.Data;

        // Legacy (v1) manifests were persisted inline as a single document; their entries live on the
        // header itself rather than in chunk documents.
        if (header.SchemaVersion < RunManifestHeader.ChunkedSchemaVersion)
        {
            return new RunManifest
            {
                SchemaVersion = header.SchemaVersion,
                DeviceId = header.DeviceId,
                RunId = header.RunId,
                Files = header.Files ?? [],
                Deleted = header.Deleted ?? []
            };
        }

        var files = new List<ManifestFileEntry>(header.FileCount);
        var deleted = new List<string>(header.DeletedCount);

        string? continuationToken = null;
        do
        {
            var page = await store.QueryAsync<RunManifestChunk>(new DocumentQuery
            {
                Type = RunManifestChunkDocument,
                PartitionKey = RunManifestPartition(deviceId, runId),
                Order = DocumentOrder.SortKeyAscending,
                PageSize = 100,
                ContinuationToken = continuationToken
            }, cancellationToken);

            foreach (var chunk in page.Items)
            {
                files.AddRange(chunk.Data.Files);
                deleted.AddRange(chunk.Data.Deleted);
            }

            continuationToken = page.NextContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));

        return new RunManifest
        {
            SchemaVersion = header.SchemaVersion,
            DeviceId = header.DeviceId,
            RunId = header.RunId,
            Files = files,
            Deleted = deleted
        };
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
