using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public sealed partial class ManifestManagerConcurrencyTests
{
    [Theory]
    [InlineData(CommitJobStatus.Queued, BackupRunStatus.Queued, 2)]
    [InlineData(CommitJobStatus.Processing, BackupRunStatus.Processing, 1)]
    [InlineData(CommitJobStatus.Succeeded, BackupRunStatus.Succeeded, 1)]
    [InlineData(CommitJobStatus.CompletedWithErrors, BackupRunStatus.CompletedWithErrors, 1)]
    [InlineData(CommitJobStatus.Failed, BackupRunStatus.Failed, 1)]
    public async Task RepeatedCommitRequest_ReturnsExistingOutcomeWithoutResettingIt(
        CommitJobStatus status, BackupRunStatus runStatus, int expectedPublications)
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid();
        var publisher = new Mock<IBackupEventPublisher>();
        var api = new BackupRunService(_service, Mock.Of<ISasUrlService>(), publisher.Object, new TelemetryProvider());
        await _service.CreateBackupRunAsync(device, run, DateTimeOffset.UtcNow);
        var originalResponse = await api.CommitBackupRunAsync(device, run);
        var persisted = await _service.GetCommitJobAsync(originalResponse.CommitId);
        persisted.Status = status;
        persisted.FilesProcessed = 7;
        persisted.FilesFailed = 2;
        persisted.AttemptCount = 3;
        persisted.CompletedAt = status is CommitJobStatus.Succeeded or CommitJobStatus.CompletedWithErrors or CommitJobStatus.Failed
            ? DateTimeOffset.UtcNow : null;
        await _service.UpdateCommitJobAsync(persisted);
        var backupRun = await _service.GetBackupRunAsync(device, run);
        backupRun.Status = runStatus;
        await _service.UpdateBackupRunAsync(device, run, backupRun);

        // The first response was lost. The retry must also work if the worker already finished.
        var retryResponse = await api.CommitBackupRunAsync(device, run);

        Assert.Equal(originalResponse.CommitId, retryResponse.CommitId);
        Assert.Equal(originalResponse.CreatedAt, retryResponse.CreatedAt);
        Assert.Equal(status, retryResponse.Status);
        Assert.Equal(persisted.ETag, retryResponse.ETag);
        Assert.Equal(persisted.CompletedAt, retryResponse.CompletedAt);
        Assert.Equal(7, retryResponse.FilesProcessed);
        Assert.Equal(2, retryResponse.FilesFailed);
        Assert.Equal(3, retryResponse.AttemptCount);
        publisher.Verify(x => x.PublishBackupRunCommittedAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedPublications));
        Assert.Single((await _service.GetCommitJobsPageAsync(new CommitJobQuery { DeviceId = device })).Commits);
    }

    [Fact]
    public async Task CommitRequest_WhenInitialPublishFails_RetryPublishesSameQueuedJob()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid();
        var publisher = new Mock<IBackupEventPublisher>();
        publisher.SetupSequence(x => x.PublishBackupRunCommittedAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Queue publish failed."))
            .Returns(Task.CompletedTask);
        var api = new BackupRunService(_service, Mock.Of<ISasUrlService>(), publisher.Object, new TelemetryProvider());
        await _service.CreateBackupRunAsync(device, run, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.CommitBackupRunAsync(device, run));
        var original = Assert.Single((await _service.GetCommitJobsPageAsync(new CommitJobQuery { DeviceId = device })).Commits);

        var retried = await api.CommitBackupRunAsync(device, run);

        Assert.Equal(original.CommitId, retried.CommitId);
        Assert.Equal(original.CreatedAt, retried.CreatedAt);
        Assert.Equal(original.ETag, retried.ETag);
        Assert.Equal(CommitJobStatus.Queued, retried.Status);
        publisher.Verify(x => x.PublishBackupRunCommittedAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ConcurrentCreation_StaleRequestReturnsWinnerWithoutOverwritingProcessingState()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid();
        var readCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeStaleRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedStore = new Mock<IStateDocumentStore>();
        delayedStore.Setup(x => x.GetAsync<CommitJob>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string type, string partition, string id, CancellationToken token) =>
            {
                var snapshot = await _store.GetAsync<CommitJob>(type, partition, id, token);
                if (snapshot == null)
                {
                    readCompleted.TrySetResult();
                    await resumeStaleRead.Task.WaitAsync(token);
                }
                return snapshot;
            });
        delayedStore.Setup(x => x.TryCreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CommitJob>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((string type, string partition, string id, CommitJob data, string? sortKey, long? sortValue, CancellationToken token) =>
                _store.TryCreateAsync(type, partition, id, data, sortKey, sortValue, token));
        delayedStore.Setup(x => x.UpsertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CommitJob>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((string type, string partition, string id, CommitJob data, string? etag, string? sortKey, long? sortValue, CancellationToken token) =>
                _store.UpsertAsync(type, partition, id, data, etag, sortKey, sortValue, token));
        var delayedManager = new ManifestManager(delayedStore.Object, NullLogger<ManifestManager>.Instance, new TelemetryProvider());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var losingRequest = delayedManager.CreateCommitJobAsync(device, run, timeout.Token);
        await readCompleted.Task.WaitAsync(timeout.Token);
        CommitJob winner;
        try
        {
            var created = await _service.CreateCommitJobAsync(device, run, timeout.Token);
            var claimed = await _service.TryClaimCommitJobAsync(created.CommitId, timeout.Token);
            Assert.True(claimed.Claimed);
            winner = claimed.CommitJob;
            winner.FilesProcessed = 42;
            winner.TotalFiles = 100;
            await _service.UpdateCommitJobAsync(winner, timeout.Token);
        }
        finally { resumeStaleRead.TrySetResult(); }
        var returned = await losingRequest;

        Assert.Equal(winner.CommitId, returned.CommitId);
        Assert.Equal(winner.CreatedAt, returned.CreatedAt);
        Assert.Equal(winner.ETag, returned.ETag);
        Assert.Equal(CommitJobStatus.Processing, returned.Status);
        Assert.Equal(42, returned.FilesProcessed);
        Assert.Equal(100, returned.TotalFiles);
        Assert.Equal(winner.LeaseExpiresAt, returned.LeaseExpiresAt);
        Assert.Equal(1, returned.AttemptCount);
        Assert.Equal(winner.ETag, (await _service.GetCommitJobAsync(winner.CommitId)).ETag);
        delayedStore.Verify(x => x.UpsertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CommitJob>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
