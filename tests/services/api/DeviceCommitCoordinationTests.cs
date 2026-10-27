using FlorisDeV.BackupContracts.State;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public sealed partial class ManifestManagerConcurrencyTests
{
    [Fact]
    public async Task SameDevice_OnlyOneCommitOwnsCatalogAtATime()
    {
        var device = Guid.NewGuid();
        var first = await CreateOrderedJob(device, -2);
        var second = await CreateOrderedJob(device, -1);
        var claim = await _service.TryClaimCommitJobAsync(first.CommitId);
        Assert.True(claim.Claimed);
        var waiting = await _service.TryClaimCommitJobAsync(second.CommitId);
        Assert.False(waiting.Claimed);
        Assert.Equal(CommitJobStatus.Queued, waiting.CommitJob.Status);
        Assert.Equal(0, (await _service.GetCommitJobAsync(second.CommitId)).AttemptCount);
        claim.CommitJob.Status = CommitJobStatus.Succeeded;
        await _service.UpdateCommitJobAsync(claim.CommitJob);
        Assert.True((await _service.TryClaimCommitJobAsync(second.CommitId)).Claimed);
    }

    [Fact]
    public async Task DifferentDevices_CanCommitConcurrently()
    {
        var first = await CreateOrderedJob(Guid.NewGuid(), -1);
        var second = await CreateOrderedJob(Guid.NewGuid(), -1);
        Assert.True((await _service.TryClaimCommitJobAsync(first.CommitId)).Claimed);
        Assert.True((await _service.TryClaimCommitJobAsync(second.CommitId)).Claimed);
    }

    [Fact]
    public async Task OlderRun_DoesNotOverwriteNewerCompletedRun()
    {
        var device = Guid.NewGuid();
        var old = await CreateOrderedJob(device, -2);
        var newer = await CreateOrderedJob(device, -1);
        var claimed = (await _service.TryClaimCommitJobAsync(newer.CommitId)).CommitJob;
        claimed.Status = CommitJobStatus.Succeeded;
        await _service.UpdateCommitJobAsync(claimed);
        var rejected = await _service.TryClaimCommitJobAsync(old.CommitId);
        Assert.False(rejected.Claimed);
        Assert.Equal(CommitJobStatus.Failed, rejected.CommitJob.Status);
        Assert.Equal("Superseded", rejected.CommitJob.FailureCategory);
        Assert.NotNull(rejected.CommitJob.DeadLetteredAt);
        Assert.Null(rejected.CommitJob.NextRetryAt);
    }

    [Fact]
    public async Task ExpiredDeviceClaim_NewOwnerRejectsOldOwnersCheckpointAndRelease()
    {
        var device = Guid.NewGuid();
        var old = await CreateOrderedJob(device, -2);
        var newer = await CreateOrderedJob(device, -1);
        var abandoned = (await _service.TryClaimCommitJobAsync(old.CommitId)).CommitJob;
        await WriteLeaseStateAsync(abandoned, DateTimeOffset.UtcNow.AddSeconds(-1));
        var winner = await _service.TryClaimCommitJobAsync(newer.CommitId);
        Assert.True(winner.Claimed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateCommitJobAsync(abandoned));
        abandoned.Status = CommitJobStatus.Failed;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateCommitJobAsync(abandoned));
        var lease = await _store.GetAsync<DeviceCommitLease>("deviceCommitLease", $"device:{device:N}", "commit");
        Assert.Equal(winner.CommitJob.ClaimId, lease!.Data.ClaimId);
    }

    [Fact]
    public async Task FailedAttempt_ReleasesDeviceForAnotherRun()
    {
        var device = Guid.NewGuid();
        var first = await CreateOrderedJob(device, -2);
        var second = await CreateOrderedJob(device, -1);
        var attempt = (await _service.TryClaimCommitJobAsync(first.CommitId)).CommitJob;
        attempt.Status = CommitJobStatus.Failed;
        attempt.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await _service.UpdateCommitJobAsync(attempt);
        Assert.True((await _service.TryClaimCommitJobAsync(second.CommitId)).Claimed);
    }

    private async Task<CommitJob> CreateOrderedJob(Guid device, int minutesAgo)
    {
        var run = Guid.NewGuid();
        await _service.CreateBackupRunAsync(device, run, DateTimeOffset.UtcNow.AddMinutes(minutesAgo));
        return await _service.CreateCommitJobAsync(device, run);
    }
}
