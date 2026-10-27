using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.State;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public sealed partial class ManifestManagerConcurrencyTests
{
    [Fact]
    public async Task ExpiredClaim_CanBeReclaimed_AndRejectsOldOwnerUpdates()
    {
        var job = await _service.CreateCommitJobAsync(Guid.NewGuid(), Guid.NewGuid());
        var first = await _service.TryClaimCommitJobAsync(job.CommitId);
        Assert.True(first.Claimed);
        Assert.False((await _service.TryClaimCommitJobAsync(job.CommitId)).Claimed);

        await WriteLeaseStateAsync(first.CommitJob, DateTimeOffset.UtcNow.AddMinutes(-1));
        var second = await _service.TryClaimCommitJobAsync(job.CommitId);
        Assert.True(second.Claimed);
        Assert.Equal(2, second.CommitJob.AttemptCount);
        Assert.True(second.CommitJob.LeaseExpiresAt > DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateCommitJobAsync(first.CommitJob));
    }

    [Fact]
    public async Task ProcessingCheckpoint_RenewsLease()
    {
        var job = await _service.CreateCommitJobAsync(Guid.NewGuid(), Guid.NewGuid());
        var claimed = (await _service.TryClaimCommitJobAsync(job.CommitId)).CommitJob;
        await WriteLeaseStateAsync(claimed, DateTimeOffset.UtcNow.AddSeconds(1));
        claimed = await _service.GetCommitJobAsync(job.CommitId);
        var renewed = await _service.UpdateCommitJobAsync(claimed);
        Assert.True(renewed.LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.False((await _service.TryClaimCommitJobAsync(job.CommitId)).Claimed);
    }

    [Fact]
    public async Task RetryableFailure_OnlyClaimsWhenDue()
    {
        var job = await _service.CreateCommitJobAsync(Guid.NewGuid(), Guid.NewGuid());
        job.Status = CommitJobStatus.Failed;
        job.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(10);
        job.AttemptCount = 1;
        await _service.UpdateCommitJobAsync(job);
        Assert.False((await _service.TryClaimCommitJobAsync(job.CommitId)).Claimed);

        job.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _service.UpdateCommitJobAsync(job);
        var retry = await _service.TryClaimCommitJobAsync(job.CommitId);
        Assert.True(retry.Claimed);
        Assert.Equal(2, retry.CommitJob.AttemptCount);
        Assert.Null(retry.CommitJob.NextRetryAt);
        Assert.Null(retry.CommitJob.CompletedAt);
    }

    [Fact]
    public async Task TerminalFailure_IsNotAutomaticallyClaimed()
    {
        var job = await _service.CreateCommitJobAsync(Guid.NewGuid(), Guid.NewGuid());
        job.Status = CommitJobStatus.Failed;
        job.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        job.DeadLetteredAt = DateTimeOffset.UtcNow;
        await _service.UpdateCommitJobAsync(job);
        Assert.False((await _service.TryClaimCommitJobAsync(job.CommitId)).Claimed);
    }

    [Fact]
    public async Task LegacyProcessingJob_RecoversUsingLastUpdatedTime()
    {
        var job = await _service.CreateCommitJobAsync(Guid.NewGuid(), Guid.NewGuid());
        job.Status = CommitJobStatus.Processing;
        job.UpdatedAt = DateTimeOffset.UtcNow.Subtract(ManifestManager.CommitLeaseDuration).AddSeconds(-1);
        await WriteLeaseStateAsync(job, null);
        Assert.True((await _service.TryClaimCommitJobAsync(job.CommitId)).Claimed);
    }

    private async Task WriteLeaseStateAsync(CommitJob job, DateTimeOffset? expiry)
    {
        // Simulate persisted state left behind by an interrupted or older worker.
        var copy = await _service.GetCommitJobAsync(job.CommitId);
        copy.Status = job.Status;
        copy.UpdatedAt = job.UpdatedAt;
        copy.LeaseExpiresAt = expiry;
        await _store.UpsertAsync("commitJob", $"commit:{job.CommitId:N}", $"{job.CommitId:N}", copy, copy.ETag);
        var deviceLease = await _store.GetAsync<DeviceCommitLease>("deviceCommitLease", $"device:{job.DeviceId:N}", "commit");
        if (deviceLease != null)
        {
            deviceLease.Data.ExpiresAt = expiry;
            await _store.UpsertAsync("deviceCommitLease", $"device:{job.DeviceId:N}", "commit", deviceLease.Data, deviceLease.ETag);
        }
    }
}
