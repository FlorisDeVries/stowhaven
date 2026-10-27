using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager
{
    private const string DeviceCommitLeaseDocument = "deviceCommitLease";
    private const string DeviceCommitLeaseId = "commit";

    private async Task<(bool Acquired, bool Superseded)> TryAcquireDeviceLeaseAsync(CommitJob job, CancellationToken token)
    {
        var partition = DevicePartition(job.DeviceId);
        var document = await store.GetAsync<DeviceCommitLease>(DeviceCommitLeaseDocument, partition, DeviceCommitLeaseId, token);
        var lease = document?.Data ?? new DeviceCommitLease { DeviceId = job.DeviceId };
        if (lease.LatestRunStartedAt is { } latest &&
            (job.RunStartedAt < latest || job.RunStartedAt == latest && job.RunId.CompareTo(lease.LatestRunId.GetValueOrDefault()) < 0))
        {
            return (false, true);
        }
        if (lease.ClaimId != null && lease.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return (false, false);
        }

        lease.CommitId = job.CommitId;
        lease.ClaimId = job.ClaimId;
        lease.ExpiresAt = DateTimeOffset.UtcNow.Add(_commitLeaseDuration);
        try
        {
            if (document == null)
            {
                return (await store.TryCreateAsync(DeviceCommitLeaseDocument, partition, DeviceCommitLeaseId, lease,
                    cancellationToken: token) != null, false);
            }
            await store.UpsertAsync(DeviceCommitLeaseDocument, partition, DeviceCommitLeaseId, lease, document.ETag,
                cancellationToken: token);
            return (true, false);
        }
        catch (StateConcurrencyException) { return (false, false); }
    }

    private async Task UpdateDeviceLeaseAsync(CommitJob job, bool release, CancellationToken token)
    {
        if (job.ClaimId == null) return; // Records written before device coordination was introduced.
        var partition = DevicePartition(job.DeviceId);
        var document = await store.GetAsync<DeviceCommitLease>(DeviceCommitLeaseDocument, partition, DeviceCommitLeaseId, token);
        if (document == null || document.Data.CommitId != job.CommitId ||
            document.Data.ClaimId != job.ClaimId && !(release && document.Data.ClaimId == null))
        {
            throw new StateConcurrencyException(DeviceCommitLeaseDocument, DeviceCommitLeaseId);
        }

        var lease = document.Data;
        if (release)
        {
            lease.ClaimId = null;
            lease.ExpiresAt = null;
            if (job.Status is CommitJobStatus.Succeeded or CommitJobStatus.CompletedWithErrors)
            {
                lease.LatestRunStartedAt = job.RunStartedAt;
                lease.LatestRunId = job.RunId;
            }
        }
        else
        {
            lease.ExpiresAt = job.LeaseExpiresAt;
        }
        await store.UpsertAsync(DeviceCommitLeaseDocument, partition, DeviceCommitLeaseId, lease, document.ETag,
            cancellationToken: token);
    }
}
