using System.Diagnostics;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupContracts.Constants;
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
        var stateKey = GetBackupRunStateKey(deviceId, runId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var state = new BackupRun
        {
            RunId = runId,
            DeviceId = deviceId,
            StartedAt = startedAt,
            Status = BackupRunStatus.Queued,
        };

        await daprClient.SaveStateAsync(DaprComponents.ManifestStateStore, stateKey, state,
            cancellationToken: cancellationToken);

        await AddBackupRunToIndexesAsync(deviceId, runId, startedAt, cancellationToken);

        telemetry.StateOperations.Add(1, new TagList { { "operation", "create" }, { "store", "manifest" } });

        LogBackupRunCreated(logger, runId, deviceId);
        return state;
    }

    public async Task<BackupRun> GetBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetBackupRun");
        var stateKey = GetBackupRunStateKey(deviceId, runId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        var (run, etag) = await daprClient.GetStateAndETagAsync<BackupRun>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (run != null)
        {
            run.ETag = etag;
            activity?.SetTag(ActivityAttributes.StateETag, etag);
            telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "result", "found" } });
            return run;
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "get" }, { "store", "manifest" }, { "result", "not_found" } });
        throw new BackupRunNotFoundException(deviceId, runId);
    }

    public async Task<BackupRun> CommitBackupRunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var stateKey = GetBackupRunStateKey(deviceId, runId);
        var (run, etag) = await daprClient.GetStateAndETagAsync<BackupRun>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        if (run == null)
        {
            throw new BackupRunNotFoundException(deviceId, runId);
        }

        if (run.Status == BackupRunStatus.Succeeded)
        {
            throw new BackupRunAlreadyCommittedException(deviceId, runId);
        }

        if (run.Status == BackupRunStatus.Failed)
        {
            throw new InvalidBackupRunStateException(deviceId, runId, run.Status, BackupRunStatus.Queued);
        }

        run.Status = BackupRunStatus.Succeeded;
        run.CompletedAt = DateTimeOffset.UtcNow;

        var success = await daprClient.TrySaveStateAsync(
            DaprComponents.ManifestStateStore,
            stateKey,
            run,
            etag,
            cancellationToken: cancellationToken);

        if (!success)
        {
            LogConcurrentUpdateDetected(logger, runId, deviceId, etag);
            throw new ConcurrentUpdateException(deviceId, runId, etag, actualETag: null);
        }

        LogBackupRunCommitted(logger, runId, deviceId, run.Status);
        return run;
    }

    public async Task<BackupRun> UpdateBackupRunAsync(Guid deviceId, Guid runId, BackupRun updatedRun,
        CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("UpdateBackupRun");
        var stateKey = GetBackupRunStateKey(deviceId, runId);

        activity?.SetTag(ActivityAttributes.StateKey, stateKey);
        activity?.SetTag(ActivityAttributes.DeviceId, deviceId.ToString());
        activity?.SetTag(ActivityAttributes.RunId, runId.ToString());

        if (!string.IsNullOrEmpty(updatedRun.ETag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                updatedRun,
                updatedRun.ETag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                LogConcurrentUpdateDetected(logger, runId, deviceId, updatedRun.ETag);
                throw new ConcurrentUpdateException(deviceId, runId, updatedRun.ETag, actualETag: null);
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                stateKey,
                updatedRun,
                cancellationToken: cancellationToken);
        }

        telemetry.StateOperations.Add(1, new TagList { { "operation", "update" }, { "store", "manifest" } });
        LogBackupRunUpdated(logger, runId, deviceId, updatedRun.Status);

        return updatedRun;
    }

    public async Task<BackupRunPage> GetBackupRunsPageAsync(BackupRunQuery query, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.ActivitySource.StartActivity("GetBackupRunsPage");
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var indexKey = query.DeviceId.HasValue
            ? GetBackupRunDeviceIndexKey(query.DeviceId.Value)
            : GetBackupRunGlobalIndexKey();

        activity?.SetTag(ActivityAttributes.StateKey, indexKey);
        activity?.SetTag("state.page_size", pageSize);

        var index = await daprClient.GetStateAsync<BackupRunIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        if (index == null || index.Runs.Count == 0)
        {
            return new BackupRunPage
            {
                Runs = [],
                PageSize = pageSize,
                ContinuationToken = query.ContinuationToken,
                NextContinuationToken = null
            };
        }

        var indexedRuns = index.Runs
            .Where(run => !query.DeviceId.HasValue || run.DeviceId == query.DeviceId.Value)
            .Where(run => !query.StartedFromUtc.HasValue || run.StartedAt >= query.StartedFromUtc.Value)
            .Where(run => !query.StartedToUtc.HasValue || run.StartedAt <= query.StartedToUtc.Value)
            .OrderByDescending(run => run.StartedAt)
            .ThenBy(run => run.DeviceId)
            .ThenBy(run => run.RunId)
            .ToArray();

        var runs = new List<BackupRun>(indexedRuns.Length);
        foreach (var indexedRun in indexedRuns)
        {
            try
            {
                var run = await GetBackupRunAsync(indexedRun.DeviceId, indexedRun.RunId, cancellationToken);
                if (!query.Status.HasValue || run.Status == query.Status.Value)
                {
                    runs.Add(run);
                }
            }
            catch (BackupRunNotFoundException)
            {
                // Keep listing resilient if an index entry points to a removed state item.
            }
        }

        var offset = DecodeContinuationToken(query.ContinuationToken);
        var pageRuns = runs
            .Skip(offset)
            .Take(pageSize)
            .ToArray();

        var nextOffset = offset + pageRuns.Length;
        return new BackupRunPage
        {
            Runs = pageRuns,
            PageSize = pageSize,
            ContinuationToken = query.ContinuationToken,
            NextContinuationToken = nextOffset < runs.Count ? EncodeContinuationToken(nextOffset) : null
        };
    }

    public async Task SaveRunManifestAsync(Guid deviceId, Guid runId, RunManifest manifest, CancellationToken cancellationToken = default)
    {
        var stateKey = GetRunManifestStateKey(deviceId, runId);
        await daprClient.SaveStateAsync(
            DaprComponents.ManifestStateStore,
            stateKey,
            manifest,
            cancellationToken: cancellationToken);

        telemetry.StateOperations.Add(1, new TagList { { "operation", "save" }, { "store", "manifest" }, { "entity", "runmanifest" } });
    }

    public async Task<RunManifest?> GetRunManifestAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken = default)
    {
        var stateKey = GetRunManifestStateKey(deviceId, runId);
        var manifest = await daprClient.GetStateAsync<RunManifest>(
            DaprComponents.ManifestStateStore,
            stateKey,
            cancellationToken: cancellationToken);

        telemetry.StateOperations.Add(1, new TagList
        {
            { "operation", "get" },
            { "store", "manifest" },
            { "entity", "runmanifest" },
            { "result", manifest == null ? "not_found" : "found" }
        });

        return manifest;
    }
}
