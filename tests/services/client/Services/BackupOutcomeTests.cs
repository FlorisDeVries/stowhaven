using System.Text.Json;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;
using Moq;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlorisDeV.BackupClient.Tests.Services;

public partial class BackupServiceTests
{
    [Fact]
    public async Task PendingJournal_IsReportedAsPendingWithItsOriginalIds()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupPendingRecovery(device, run, commit, new CommitStatusResponse
            { DeviceId = device, RunId = run, CommitId = commit, Status = CommitJobStatus.Processing });
        var settings = Options.Create(new BackupClientOptions { BackupTargets = new() { ["fixture"] = _testDirectory }, CommitStatusTimeoutSeconds = 0 });
        var service = new BackupService(_mockLogger.Object, _telemetryProvider, _mockApiClient.Object, _mockStateService.Object,
            _mockScanner.Object, _mockUploader.Object, settings);
        var result = await service.RunAsync(CancellationToken.None);
        Assert.Equal(BackupOutcome.Pending, result.Outcome);
        Assert.Equal(run, result.RunId);
        Assert.Equal(commit, result.CommitId);
        Assert.Equal(2, result.ExitCode);
        _mockStateService.Verify(x => x.SaveBackupSuccessAsync(It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<FileMetadata>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SkippedFiles_AreReportedAsPartialEvenWithoutAnUpload()
    {
        var device = Guid.NewGuid();
        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(device, null, null, null, 0, 0));
        var file = new TaggedFile("fixture", _testDirectory, new FileMetadata(Path.Combine(_testDirectory, "locked.txt"), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        _mockScanner.Setup(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>())).Returns(ToAsync(file));
        _mockScanner.Setup(x => x.AnalyzeFileAsync(file, It.IsAny<CancellationToken>())).ReturnsAsync((file, false, FileChangeType.Skipped));
        var result = await _sut.RunAsync(CancellationToken.None);
        Assert.Equal(BackupOutcome.CompletedWithErrors, result.Outcome);
        Assert.Equal(1, result.FilesSkipped);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task NoChanges_HasExplicitOutcomeAndSuccessExitCode()
    {
        var device = Guid.NewGuid();
        _mockStateService.Setup(x => x.GetOrCreateDeviceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState(device, null, null, null, 0, 0));
        _mockScanner.Setup(x => x.ScanAllTargetsAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsync<TaggedFile>());
        var result = await _sut.RunAsync(CancellationToken.None);
        Assert.Equal(BackupOutcome.NoChanges, result.Outcome);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task CompletedJournal_IsReportedAsCompletedRatherThanJustSubmitted()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        SetupPendingRecovery(device, run, commit, new CommitStatusResponse
            { DeviceId = device, RunId = run, CommitId = commit, Status = CommitJobStatus.Succeeded });
        var result = await _sut.RunAsync(CancellationToken.None);
        Assert.Equal(BackupOutcome.Succeeded, result.Outcome);
        Assert.Equal(run, result.RunId);
        Assert.Equal(commit, result.CommitId);
    }

    [Theory]
    [InlineData(BackupOutcome.NoChanges, 0)]
    [InlineData(BackupOutcome.Succeeded, 0)]
    [InlineData(BackupOutcome.Failed, 1)]
    [InlineData(BackupOutcome.Pending, 2)]
    [InlineData(BackupOutcome.CompletedWithErrors, 3)]
    public async Task ResultFile_HasMachineReadableOutcomeAndExitCode(BackupOutcome outcome, int exitCode)
    {
        var path = Path.Combine(_testDirectory, "result.json");
        await new BackupResult { Outcome = outcome, RunId = Guid.NewGuid() }.WriteAsync(path);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(exitCode, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()), document.RootElement.GetProperty("outcome").GetString());
        Assert.Empty(Directory.GetFiles(_testDirectory, "*.tmp"));
    }
}
