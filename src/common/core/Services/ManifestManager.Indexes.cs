using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FlorisDeV.BackupContracts.Constants;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager
{
    private async Task AddFileEntryToIndexAsync(Guid deviceId, string relativePath, CancellationToken cancellationToken)
    {
        var indexKey = GetFileEntryIndexKey(deviceId);
        var (index, etag) = await daprClient.GetStateAndETagAsync<FileEntryIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        index ??= new FileEntryIndex
        {
            DeviceId = deviceId,
            RelativePaths = []
        };

        if (index.RelativePaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        index.RelativePaths.Add(relativePath);
        index.RelativePaths.Sort(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(etag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                etag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                throw new InvalidOperationException($"Concurrent update detected for file entry index of device {deviceId}");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                cancellationToken: cancellationToken);
        }
    }

    private async Task AddBackupRunToIndexesAsync(Guid deviceId, Guid runId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var entry = new BackupRunIndexEntry
        {
            DeviceId = deviceId,
            RunId = runId,
            StartedAt = startedAt
        };

        await AddBackupRunToIndexAsync(GetBackupRunGlobalIndexKey(), entry, cancellationToken);
        await AddBackupRunToIndexAsync(GetBackupRunDeviceIndexKey(deviceId), entry, cancellationToken);
    }

    private async Task AddBackupRunToIndexAsync(string indexKey, BackupRunIndexEntry entry, CancellationToken cancellationToken)
    {
        var (index, etag) = await daprClient.GetStateAndETagAsync<BackupRunIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        index ??= new BackupRunIndex { Runs = [] };

        if (index.Runs.Any(run => run.DeviceId == entry.DeviceId && run.RunId == entry.RunId))
        {
            return;
        }

        index.Runs.Add(entry);
        index.Runs.Sort((left, right) => right.StartedAt.CompareTo(left.StartedAt));

        if (!string.IsNullOrEmpty(etag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                etag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                throw new InvalidOperationException($"Concurrent update detected for backup run index '{indexKey}'");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                cancellationToken: cancellationToken);
        }
    }

    private async Task AddCommitJobToIndexesAsync(CommitJob commitJob, CancellationToken cancellationToken)
    {
        var entry = new CommitJobIndexEntry
        {
            CommitId = commitJob.CommitId,
            DeviceId = commitJob.DeviceId,
            RunId = commitJob.RunId,
            CreatedAt = commitJob.CreatedAt
        };

        await AddCommitJobToIndexAsync(GetCommitJobGlobalIndexKey(), entry, cancellationToken);
        await AddCommitJobToIndexAsync(GetCommitJobDeviceIndexKey(commitJob.DeviceId), entry, cancellationToken);
        await AddCommitJobToIndexAsync(GetCommitJobRunIndexKey(commitJob.DeviceId, commitJob.RunId), entry, cancellationToken);
    }

    private async Task AddCommitJobToIndexAsync(string indexKey, CommitJobIndexEntry entry, CancellationToken cancellationToken)
    {
        var (index, etag) = await daprClient.GetStateAndETagAsync<CommitJobIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        index ??= new CommitJobIndex { Commits = [] };

        if (index.Commits.Any(commit => commit.CommitId == entry.CommitId))
        {
            return;
        }

        index.Commits.Add(entry);
        index.Commits.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));

        if (!string.IsNullOrEmpty(etag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                etag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                throw new InvalidOperationException($"Concurrent update detected for commit job index '{indexKey}'");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                cancellationToken: cancellationToken);
        }
    }

    private async Task AddCommitFileProgressToIndexAsync(CommitFileProgress progress, CancellationToken cancellationToken)
    {
        var indexKey = GetCommitFileProgressIndexKey(progress.CommitId);
        var (index, etag) = await daprClient.GetStateAndETagAsync<CommitFileProgressIndex>(
            DaprComponents.ManifestStateStore,
            indexKey,
            cancellationToken: cancellationToken);

        index ??= new CommitFileProgressIndex
        {
            CommitId = progress.CommitId,
            UniqueFileIds = []
        };

        if (index.UniqueFileIds.Contains(progress.UniqueFileId, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        index.UniqueFileIds.Add(progress.UniqueFileId);
        index.UniqueFileIds.Sort(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(etag))
        {
            var success = await daprClient.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                etag,
                cancellationToken: cancellationToken);

            if (!success)
            {
                throw new InvalidOperationException($"Concurrent update detected for commit file progress index of commit {progress.CommitId}");
            }
        }
        else
        {
            await daprClient.SaveStateAsync(
                DaprComponents.ManifestStateStore,
                indexKey,
                index,
                cancellationToken: cancellationToken);
        }
    }

    private static string GetFileEntryIndexKey(Guid deviceId) => $"{deviceId}/files/_index";

    private static string GetBackupRunGlobalIndexKey() => "backupruns/_index";

    private static string GetBackupRunDeviceIndexKey(Guid deviceId) => $"{deviceId}/backupruns/_index";

    private static string GetRunManifestStateKey(Guid deviceId, Guid runId) => $"{deviceId}/runmanifests/{runId}";

    private static string GetCommitJobGlobalIndexKey() => "commitjobs/_index";

    private static string GetCommitJobDeviceIndexKey(Guid deviceId) => $"{deviceId}/commitjobs/_index";

    private static string GetCommitJobRunIndexKey(Guid deviceId, Guid runId) => $"{deviceId}/backupruns/{runId}/commitjobs/_index";

    private static string GetCommitFileProgressIndexKey(Guid commitId) => $"commitjobs/{commitId}/files/_index";

    private static string GetCommitJobStateKey(Guid commitId) => $"commitjobs/{commitId}";

    private static string GetCommitFileProgressStateKey(Guid commitId, string uniqueFileId) => $"commitjobs/{commitId}/files/{uniqueFileId}";

    private static string EncodeContinuationToken(int offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static int DecodeContinuationToken(string? continuationToken)
    {
        if (string.IsNullOrWhiteSpace(continuationToken))
        {
            return 0;
        }

        try
        {
            var tokenBytes = Convert.FromBase64String(continuationToken);
            var tokenText = Encoding.UTF8.GetString(tokenBytes);
            if (int.TryParse(tokenText, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0)
            {
                return offset;
            }
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("Invalid continuation token.", nameof(continuationToken));
    }

    private static Guid CreateDeterministicCommitId(Guid deviceId, Guid runId)
    {
        var input = Encoding.UTF8.GetBytes($"{deviceId:N}:{runId:N}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        return new Guid(guidBytes);
    }
}
