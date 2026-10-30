using System.Runtime.CompilerServices;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FlorisDeV.BackupClient.Tests.Services;

public partial class BackupServiceTests
{
    [Fact]
    public async Task LostCommitResponse_ResumesCompletedJobWithoutStartingAnotherRun()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupPendingRecovery(device, run, commit, new CommitStatusResponse
        {
            CommitId = commit, DeviceId = device, RunId = run, Status = CommitJobStatus.Succeeded
        });
        // The manifest was submitted, but the initial commit response never reached the journal.
        _mockStateService.Setup(x => x.GetPendingBackupRunAsync(device, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePendingRun(device, run, commit) with { CommitId = null });
        _mockApiClient.Setup(x => x.CommitBackupRun(device,
                It.Is<FlorisDeV.BackupContracts.Api.Requests.CommitBackupRunRequest>(request => request.RunId == run),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitBackupRunResponse
                { CommitId = commit, DeviceId = device, RunId = run, Status = CommitJobStatus.Succeeded });

        Assert.True(await _sut.Backup(CancellationToken.None));

        _mockStateService.Verify(x => x.SavePendingBackupRunAsync(
            It.Is<PendingBackupRun>(pending => pending.RunId == run && pending.CommitId == commit && pending.ManifestUploaded),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockApiClient.Verify(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockScanner.Verify(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string[]?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TerminallyFailedPendingCommit_ReconcilesAndContinuesScanning()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupPendingRecovery(device, run, commit, new CommitStatusResponse
        {
            CommitId = commit, DeviceId = device, RunId = run, Status = CommitJobStatus.Failed,
            DeadLetteredAt = DateTimeOffset.UtcNow, Error = "Attempt limit reached"
        });
        Assert.True(await _sut.Backup(CancellationToken.None));
        _mockStateService.Verify(x => x.RecoverFailedRunAsync(device, run, It.IsAny<CancellationToken>()), Times.Once);
        _mockScanner.Verify(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string[]?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryableFailedCommit_KeepsJournalAndWaitsForServerRecovery()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupPendingRecovery(device, run, commit, new CommitStatusResponse
        {
            CommitId = commit, DeviceId = device, RunId = run, Status = CommitJobStatus.Failed,
            NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(2)
        });
        var options = Options.Create(new BackupClientOptions
        {
            BackupTargets = new() { ["default"] = _testDirectory }, CommitStatusTimeoutSeconds = 0
        });
        var sut = new BackupService(_mockLogger.Object, _telemetryProvider, _mockApiClient.Object,
            _mockStateService.Object, _mockScanner.Object, _mockUploader.Object, options);
        Assert.True(await sut.Backup(CancellationToken.None));
        _mockStateService.Verify(x => x.RecoverFailedRunAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockScanner.Verify(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string[]?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IncompleteScan_NeverComputesOrCommitsDeletions()
    {
        var device = Guid.NewGuid();
        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(device, null, null, null, 0, 0));
        _mockScanner.Setup(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string[]?>(), It.IsAny<CancellationToken>())).Returns(IncompleteScan());
        _mockScanner.Setup(x => x.AnalyzeFileAsync(It.IsAny<TaggedFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaggedFile f, CancellationToken _) => (f, false, FileChangeType.Unchanged));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sut.Backup(CancellationToken.None));
        _mockStateService.Verify(x => x.CountScanDeletionsAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockStateService.Verify(x => x.RecordScanDeletionsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApiClient.Verify(x => x.CommitBackupRun(It.IsAny<Guid>(),
            It.IsAny<FlorisDeV.BackupContracts.Api.Requests.CommitBackupRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async IAsyncEnumerable<TaggedFile> IncompleteScan([EnumeratorCancellation] CancellationToken token = default)
    {
        await Task.Yield();
        yield return new TaggedFile("default", _testDirectory,
            new FileMetadata(Path.Combine(_testDirectory, "visible.txt"), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        throw new UnauthorizedAccessException("A subtree could not be enumerated.");
    }

    private void SetupPendingRecovery(Guid device, Guid run, Guid commit, CommitStatusResponse status)
    {
        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(device, null, null, null, 0, 0));
        _mockStateService.Setup(x => x.GetPendingBackupRunAsync(device, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePendingRun(device, run, commit));
        _mockApiClient.Setup(x => x.GetCommitStatus(device, commit, It.IsAny<CancellationToken>())).ReturnsAsync(status);
        _mockScanner.Setup(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string[]?>(), It.IsAny<CancellationToken>())).Returns(ToAsync<TaggedFile>());
    }
}
