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

    public async Task SaveRunManifestAsync(Guid deviceId, Guid runId, RunManifest manifest, CancellationToken cancellationToken = default)
    {
        await store.UpsertAsync(
            RunManifestDocument, DevicePartition(deviceId), $"{runId:N}", manifest,
            cancellationToken: cancellationToken);

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "runmanifest" } });
    }

    public async Task<RunManifest?> GetRunManifestAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var document = await store.GetAsync<RunManifest>(
            RunManifestDocument, DevicePartition(deviceId), $"{runId:N}", cancellationToken);

        telemetry.StateOperations.Add(1, new TagList
        {
            { "operation", "get" },
            { "store", "manifest" },
            { "entity", "runmanifest" },
            { "result", document == null ? "not_found" : "found" }
        });

        return document?.Data;
    }

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
