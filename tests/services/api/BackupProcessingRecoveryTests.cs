using System.Text.Json;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.BackupWorker.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public partial class BackupProcessingServiceTests
{
    [Fact]
    public async Task CatalogAlreadyPointsToIncomingVersion_ReplayNeverRetiresIt()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupQueuedCommitJob(commit, device, run);
        SetupBackupRun(device, run);
        var incoming = CreateFileEntry("documents/file.txt", "incoming");
        SetupManifestDownload(CreateManifest(device, run, [incoming], []));
        _manifestManagerMock.Setup(x => x.GetFileEntryAsync(device, incoming.LogicalPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileEntry
            {
                DeviceId = device, RelativePath = incoming.LogicalPath, CurrentVersionId = incoming.UniqueFileId,
                Size = incoming.Size, LastWriteUtc = incoming.Mtime, LastBackupRunId = run.ToString("N")
            });

        await _sut.ProcessBackupRunAsync(CreateBackupEvent(device, run, commit));

        _manifestManagerMock.Verify(x => x.SaveFileVersionAsync(
            It.Is<FileVersion>(v => v.UniqueFileId == incoming.UniqueFileId && v.State == FileVersionState.Retired),
            It.IsAny<CancellationToken>()), Times.Never);
        _blobStorageServiceMock.Verify(x => x.MoveBlobAsync($"devices/{device:N}/files/incoming",
            It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InterruptedRetirement_ReplayUsesPersistedPreviousVersion()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupQueuedCommitJob(commit, device, run);
        SetupBackupRun(device, run);
        var incoming = CreateFileEntry("documents/file.txt", "incoming");
        SetupManifestDownload(CreateManifest(device, run, [incoming], []));
        var current = new FileEntry
        {
            DeviceId = device, RelativePath = incoming.LogicalPath, CurrentVersionId = "previous",
            Size = 1, LastWriteUtc = incoming.Mtime, LastBackupRunId = Guid.NewGuid().ToString("N")
        };
        var versions = new Dictionary<string, FileVersion>
        {
            ["previous"] = new FileVersion
            {
                DeviceId = device, UniqueFileId = "previous", RelativePath = incoming.LogicalPath,
                Sha256 = "previous-hash", Size = 1, CreatedAt = DateTimeOffset.UtcNow, State = FileVersionState.Active
            }
        };
        CommitFileProgress? persistedProgress = null;
        _manifestManagerMock.Setup(x => x.GetFileEntryAsync(device, incoming.LogicalPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => current);
        _manifestManagerMock.Setup(x => x.SaveFileEntryAsync(It.IsAny<FileEntry>(), It.IsAny<CancellationToken>()))
            .Callback<FileEntry, CancellationToken>((entry, _) => current = entry).Returns(Task.CompletedTask);
        _manifestManagerMock.Setup(x => x.GetFileVersionAsync(device, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string id, CancellationToken _) => versions.GetValueOrDefault(id));
        _manifestManagerMock.Setup(x => x.SaveFileVersionAsync(It.IsAny<FileVersion>(), It.IsAny<CancellationToken>()))
            .Callback<FileVersion, CancellationToken>((version, _) => versions[version.UniqueFileId] = version).Returns(Task.CompletedTask);
        _manifestManagerMock.Setup(x => x.GetCommitFileProgressAsync(commit, incoming.UniqueFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persistedProgress);
        _manifestManagerMock.Setup(x => x.SaveCommitFileProgressAsync(It.IsAny<CommitFileProgress>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommitFileProgress progress, CancellationToken _) =>
            {
                persistedProgress = JsonSerializer.Deserialize<CommitFileProgress>(JsonSerializer.Serialize(progress));
                return progress;
            });
        _manifestManagerMock.Setup(x => x.TryClaimCommitJobAsync(commit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (true, new CommitJob
            {
                CommitId = commit, DeviceId = device, RunId = run, Status = CommitJobStatus.Processing, AttemptCount = 1
            }));
        var oldSource = $"devices/{device:N}/files/previous";
        var interrupt = true;
        _blobStorageServiceMock.Setup(x => x.MoveBlobAsync(oldSource, It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (interrupt) { interrupt = false; throw new OperationCanceledException("Stopped after publishing catalog entry."); }
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.ProcessBackupRunAsync(CreateBackupEvent(device, run, commit)));
        Assert.Equal("incoming", current.CurrentVersionId);
        Assert.Equal("previous", persistedProgress!.PreviousVersionId);
        Assert.Equal(FileVersionState.Active, versions["previous"].State);

        await _sut.ProcessBackupRunAsync(CreateBackupEvent(device, run, commit));
        Assert.Equal("incoming", current.CurrentVersionId);
        Assert.Equal(FileVersionState.Active, versions["incoming"].State);
        Assert.Equal(FileVersionState.Retired, versions["previous"].State);
        Assert.Equal(CommitFileStatus.Succeeded, persistedProgress!.Status);
    }

    [Fact]
    public async Task RetryWindowNotDue_DoesNotAcknowledgeOrChangeTheJob()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        _manifestManagerMock.Setup(x => x.TryClaimCommitJobAsync(commit, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, new CommitJob { Status = CommitJobStatus.Failed, NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(1) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ProcessBackupRunAsync(CreateBackupEvent(device, run, commit)));
        _manifestManagerMock.Verify(x => x.UpdateCommitJobAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LongManifestPersistence_RenewsClaimBeforeFileCheckpoints()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupQueuedCommitJob(commit, device, run);
        SetupBackupRun(device, run);
        SetupManifestDownload(CreateManifest(device, run, [], []));
        var releaseManifest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manifestManagerMock.Setup(x => x.SaveRunManifestAsync(device, run,
                It.IsAny<IAsyncEnumerable<FlorisDeV.BackupApi.Services.RunManifestStreamItem>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, IAsyncEnumerable<FlorisDeV.BackupApi.Services.RunManifestStreamItem> _, CancellationToken token) =>
            {
                await releaseManifest.Task.WaitAsync(token);
                return (0, 0);
            });
        _manifestManagerMock.Setup(x => x.UpdateCommitJobAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommitJob job, CancellationToken _) => { renewed.TrySetResult(); return job; });
        var worker = new BackupProcessingService(_loggerMock.Object, _blobStorageServiceMock.Object,
            _manifestManagerMock.Object, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["CommitProcessing:HeartbeatIntervalSeconds"] = "1" }).Build(), _telemetryMock.Object);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var processing = worker.ProcessBackupRunAsync(CreateBackupEvent(device, run, commit), timeout.Token);
        try { await renewed.Task.WaitAsync(timeout.Token); }
        finally { releaseManifest.TrySetResult(); }
        await processing;
    }

    [Fact]
    public async Task AttemptLimitReached_RecordsTerminalFailureWithoutAnotherAutomaticRetry()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupQueuedCommitJob(commit, device, run);
        SetupBackupRun(device, run);
        var job = new CommitJob { CommitId = commit, DeviceId = device, RunId = run,
            Status = CommitJobStatus.Processing, AttemptCount = 5 };
        _manifestManagerMock.Setup(x => x.TryClaimCommitJobAsync(commit, It.IsAny<CancellationToken>())).ReturnsAsync((true, job));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ProcessBackupRunAsync(CreateBackupEvent(device, run, commit)));
        Assert.Equal(CommitJobStatus.Failed, job.Status);
        Assert.NotNull(job.DeadLetteredAt);
        Assert.Null(job.NextRetryAt);
    }

    [Fact]
    public async Task LostLease_CancelsProcessingWithoutReloadingAnotherOwnersJob()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupQueuedCommitJob(commit, device, run);
        SetupBackupRun(device, run);
        SetupManifestDownload(CreateManifest(device, run, [], []));
        _manifestManagerMock.Setup(x => x.SaveRunManifestAsync(device, run,
                It.IsAny<IAsyncEnumerable<FlorisDeV.BackupApi.Services.RunManifestStreamItem>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, IAsyncEnumerable<FlorisDeV.BackupApi.Services.RunManifestStreamItem> _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return (0, 0);
            });
        _manifestManagerMock.Setup(x => x.UpdateCommitJobAsync(It.IsAny<CommitJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The saved ETag belongs to an expired claim."));
        var worker = new BackupProcessingService(_loggerMock.Object, _blobStorageServiceMock.Object,
            _manifestManagerMock.Object, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["CommitProcessing:HeartbeatIntervalSeconds"] = "1" }).Build(), _telemetryMock.Object);
        var processing = worker.ProcessBackupRunAsync(CreateBackupEvent(device, run, commit));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(10)));

        _manifestManagerMock.Verify(x => x.GetCommitJobAsync(commit, It.IsAny<CancellationToken>()), Times.Never);
        _manifestManagerMock.Verify(x => x.SaveFileEntryAsync(It.IsAny<FileEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
