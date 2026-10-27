using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public class OperationalServiceRetryTests
{
    [Fact]
    public async Task ExplicitRetry_AfterAttemptLimit_StartsFreshBoundedSequence()
    {
        var job = new CommitJob { CommitId = Guid.NewGuid(), Status = CommitJobStatus.Failed,
            AttemptCount = 5, DeadLetteredAt = DateTimeOffset.UtcNow };
        var manager = new Mock<IManifestManager>();
        var publisher = new Mock<IBackupEventPublisher>();
        manager.Setup(x => x.GetCommitJobAsync(job.CommitId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        manager.Setup(x => x.UpdateCommitJobAsync(job, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var service = new OperationalService(manager.Object, publisher.Object, Mock.Of<IBlobStorageService>(),
            NullLogger<OperationalService>.Instance);
        await service.RetryCommitJobAsync(job.CommitId);
        Assert.Equal(CommitJobStatus.Queued, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Null(job.DeadLetteredAt);
        publisher.Verify(x => x.PublishBackupRunCommittedAsync(job, It.IsAny<CancellationToken>()), Times.Once);
    }
}
