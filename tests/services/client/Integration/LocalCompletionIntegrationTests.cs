using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FlorisDeV.BackupClient.Tests.Integration;

public partial class BackupServiceIntegrationTests
{
    [Theory]
    [InlineData("device")]
    [InlineData("run")]
    [InlineData("commit")]
    public async Task LocalCompletion_UnrelatedRejectedEntryCannotPrunePendingJournal(string mismatch)
    {
        var path = Path.Combine(_testRoot, "state.db");
        using var state = OpenState(path);
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid();
        var commit = Guid.NewGuid();
        _committedRuns[commit] = run;
        await StageRunAsync(state, device, run, commit);
        await state.AppendPendingRunFilesAsync(device, run,
            [new TaggedFile("default", "/synthetic", new FileMetadata("/synthetic/rejected.txt", 5,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash")) { UniqueFileId = "rejected" }]);
        ConfigurePartialStatus();
        var page = FailedPage(device, run, commit);
        var unrelated = page.Files[0] with
        {
            DeviceId = mismatch == "device" ? Guid.NewGuid() : device,
            RunId = mismatch == "run" ? Guid.NewGuid() : run,
            CommitId = mismatch == "commit" ? Guid.NewGuid() : commit
        };
        _mockApiClient.Setup(x => x.ListFailedCommitFiles(device, commit, 500,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page with { Files = [unrelated] });

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateServiceWithState(state).RunAsync(CancellationToken.None));

        using var reopened = OpenState(path);
        Assert.Equal(1, (await reopened.GetPendingBackupRunAsync(device))!.UploadedFileCount);
        Assert.Empty(await reopened.GetAllFileStatesAsync());
        Assert.Null((await reopened.GetOrCreateDeviceStateAsync()).LastRunId);
    }

    [Theory]
    [InlineData(CommitJobStatus.Queued)]
    [InlineData(CommitJobStatus.Processing)]
    [InlineData(CommitJobStatus.Failed)]
    public async Task LocalCompletion_NonterminalCommitRetainsPendingState(CommitJobStatus status)
    {
        var path = Path.Combine(_testRoot, "state.db");
        using var state = OpenState(path);
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid();
        var commit = Guid.NewGuid();
        await StageRunAsync(state, device, run, commit);
        _mockApiClient.Setup(x => x.GetCommitStatus(device, commit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitStatusResponse
            {
                DeviceId = device, RunId = run, CommitId = commit, Status = status,
                NextRetryAt = status == CommitJobStatus.Failed ? DateTimeOffset.UtcNow.AddMinutes(1) : null
            });

        Assert.Equal(BackupOutcome.Pending, (await CreateServiceWithState(state).RunAsync(CancellationToken.None)).Outcome);

        using var reopened = OpenState(path);
        Assert.Equal(run, (await reopened.GetPendingBackupRunAsync(device))!.RunId);
        Assert.Null((await reopened.GetOrCreateDeviceStateAsync()).LastRunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalCompletion_NormalAndResumedRunsPersistAcceptedFiles(bool resumed)
    {
        var path = Path.Combine(_testRoot, "state.db");
        using var state = OpenState(path);
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var service = CreateServiceWithState(state);
        var file = await CreateSourceFileAsync("accepted.txt", "accepted content");
        if (resumed)
        {
            var run = Guid.NewGuid();
            var commit = Guid.NewGuid();
            _committedRuns[commit] = run;
            await StageRunAsync(state, device, run, commit);
            await state.AppendPendingRunFilesAsync(device, run, [file]);
        }

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(BackupOutcome.Succeeded, result.Outcome);
        using var reopened = OpenState(path);
        var completed = await reopened.GetOrCreateDeviceStateAsync();
        Assert.Equal(result.RunId, completed.LastRunId);
        Assert.Equal(result.CommitId!.Value.ToString("N"), completed.LastCommitId);
        Assert.Equal(1, completed.TotalFilesTracked);
        Assert.Equal(16, completed.TotalBytesTracked);
        Assert.Equal(file.Metadata.Hash, (await reopened.GetFileStateAsync(file.GetStoragePath()))!.Sha256Hash);
        Assert.Null(await reopened.GetPendingBackupRunAsync(device));
        if (resumed) Assert.Empty(_testUploader.UploadedPaths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalCompletion_PartialSuccessKeepsRejectedFileEligibleForRetry(bool hasAcceptedFile)
    {
        var path = Path.Combine(_testRoot, "state.db");
        using var state = OpenState(path);
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var service = CreateServiceWithState(state);
        if (hasAcceptedFile) await CreateSourceFileAsync("accepted.txt", "accepted content");
        await CreateSourceFileAsync("rejected.txt", "rejected content");
        ConfigurePartialStatus();
        _mockApiClient.Setup(x => x.ListFailedCommitFiles(device, It.IsAny<Guid>(), 500,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid d, Guid c, int _, string? _, CancellationToken _) => FailedPage(d, _committedRuns[c], c));

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(BackupOutcome.CompletedWithErrors, result.Outcome);
        using var reopened = OpenState(path);
        Assert.Equal(hasAcceptedFile, await reopened.GetFileStateAsync("default/accepted.txt") is not null);
        Assert.Null(await reopened.GetFileStateAsync("default/rejected.txt"));
        Assert.Equal(hasAcceptedFile ? 1 : 0, (await reopened.GetOrCreateDeviceStateAsync()).TotalFilesTracked);
        Assert.Equal(hasAcceptedFile ? 16 : 0, (await reopened.GetOrCreateDeviceStateAsync()).TotalBytesTracked);
        Assert.Null(await reopened.GetPendingBackupRunAsync(device));
        var scanner = new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), reopened,
            NullLogger<BackupScanner>.Instance);
        var rejected = await CreateSourceFileAsync("rejected.txt", "rejected content");
        Assert.True((await scanner.AnalyzeFileAsync(rejected, CancellationToken.None)).NeedsBackup);
    }

    [Fact]
    public async Task LocalCompletion_IncompleteRejectionPagesRetainPendingRunAndCanResume()
    {
        var path = Path.Combine(_testRoot, "state.db");
        using var state = OpenState(path);
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var service = CreateServiceWithState(state);
        var accepted = await CreateSourceFileAsync("accepted.txt", "accepted content");
        var rejected = await CreateSourceFileAsync("rejected.txt", "rejected content");
        var run = Guid.NewGuid();
        var commit = Guid.NewGuid();
        _committedRuns[commit] = run;
        await StageRunAsync(state, device, run, commit);
        await state.AppendPendingRunFilesAsync(device, run, [accepted, rejected]);
        ConfigurePartialStatus();
        var secondPageAvailable = false;
        _mockApiClient.Setup(x => x.ListFailedCommitFiles(device, commit, 500,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid d, Guid c, int _, string? token, CancellationToken _) =>
                token == null ? FailedPage(d, run, c) with { NextContinuationToken = "next" }
                : secondPageAvailable ? FailedPage(d, run, c) with { Files = [] }
                : throw new HttpRequestException("Interrupted rejection enumeration"));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RunAsync(CancellationToken.None));

        using (var reopened = OpenState(path))
        {
            Assert.Equal(run, (await reopened.GetPendingBackupRunAsync(device))!.RunId);
            Assert.Null((await reopened.GetOrCreateDeviceStateAsync()).LastRunId);
            Assert.Empty(await reopened.GetAllFileStatesAsync());
            var remaining = new List<TaggedFile>();
            await foreach (var item in reopened.StreamPendingRunFilesAsync(device, run)) remaining.Add(item);
            Assert.Equal(accepted.GetStoragePath(), Assert.Single(remaining).GetStoragePath());
        }
        secondPageAvailable = true;
        // The rejected path can still be scanned; make subsequent fresh uploads remain pending.
        // Observe the completed resumed state before scanning starts a fresh run.
        _mockApiClient.Setup(x => x.StartBackupRun(device, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Stop after resumed completion"));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.RunAsync(CancellationToken.None));
        using var completed = OpenState(path);
        Assert.Equal(run, (await completed.GetOrCreateDeviceStateAsync()).LastRunId);
        Assert.NotNull(await completed.GetFileStateAsync(accepted.GetStoragePath()));
        Assert.Null(await completed.GetFileStateAsync(rejected.GetStoragePath()));
        Assert.Null(await completed.GetPendingBackupRunAsync(device));
    }

    [Theory]
    [InlineData(false, "device")]
    [InlineData(false, "run")]
    [InlineData(false, "empty-commit")]
    [InlineData(true, "device")]
    [InlineData(true, "run")]
    [InlineData(true, "empty-commit")]
    public async Task LocalCompletion_MismatchedSubmissionDoesNotPersistCommitIdentity(bool resumed, string mismatch)
    {
        var path = Path.Combine(_testRoot, "state.db");
        using var state = OpenState(path);
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var service = CreateServiceWithState(state);
        if (resumed) await StageRunAsync(state, device, Guid.NewGuid(), null);
        else await CreateSourceFileAsync("accepted.txt", "accepted content");
        _mockApiClient.Setup(x => x.CommitBackupRun(device, It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid d, CommitBackupRunRequest request, CancellationToken _) => new CommitBackupRunResponse
            {
                DeviceId = mismatch == "device" ? Guid.NewGuid() : d,
                RunId = mismatch == "run" ? Guid.NewGuid() : request.RunId,
                CommitId = mismatch == "empty-commit" ? Guid.Empty : Guid.NewGuid()
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(CancellationToken.None));

        using var reopened = OpenState(path);
        var pending = await reopened.GetPendingBackupRunAsync(device);
        Assert.NotNull(pending);
        Assert.Null(pending.CommitId);
        Assert.True(pending.ManifestUploaded);
        Assert.Null((await reopened.GetOrCreateDeviceStateAsync()).LastRunId);
        Assert.Empty(await reopened.GetAllFileStatesAsync());
        _mockApiClient.Verify(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("run")]
    [InlineData("commit")]
    public async Task LocalCompletion_MismatchedStatusPreservesPendingState(string mismatch)
    {
        var path = Path.Combine(_testRoot, "state.db");
        using var state = OpenState(path);
        var device = (await state.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid();
        var commit = Guid.NewGuid();
        await StageRunAsync(state, device, run, commit);
        _mockApiClient.Setup(x => x.GetCommitStatus(device, commit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitStatusResponse
            {
                DeviceId = mismatch == "device" ? Guid.NewGuid() : device,
                RunId = mismatch == "run" ? Guid.NewGuid() : run,
                CommitId = mismatch == "commit" ? Guid.NewGuid() : commit,
                Status = CommitJobStatus.CompletedWithErrors, FilesFailed = 1
            });
        var service = CreateServiceWithState(state);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(CancellationToken.None));

        using var reopened = OpenState(path);
        Assert.Equal(run, (await reopened.GetPendingBackupRunAsync(device))!.RunId);
        Assert.Null((await reopened.GetOrCreateDeviceStateAsync()).LastRunId);
        Assert.Empty(await reopened.GetAllFileStatesAsync());
        _mockApiClient.Verify(x => x.ListFailedCommitFiles(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static BackupStateService OpenState(string path) => new(
        Options.Create(new DatabaseOptions { FilePath = path }), NullLogger<BackupStateService>.Instance);

    private void ConfigurePartialStatus() => _mockApiClient
        .Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((Guid d, Guid c, CancellationToken _) => new CommitStatusResponse
        {
            DeviceId = d, RunId = _committedRuns[c], CommitId = c,
            Status = CommitJobStatus.CompletedWithErrors, FilesFailed = 1
        });

    private static ListCommitFileProgressResponse FailedPage(Guid device, Guid run, Guid commit) => new()
    {
        DeviceId = device, RunId = run, CommitId = commit, PageSize = 500,
        Files = [new CommitFileProgressResponse
        {
            DeviceId = device, RunId = run, CommitId = commit, LogicalPath = "default/rejected.txt",
            UniqueFileId = "rejected", Status = CommitFileStatus.Failed, UpdatedAt = DateTimeOffset.UtcNow
        }]
    };

    private async Task<TaggedFile> CreateSourceFileAsync(string name, string content)
    {
        var directory = Path.Combine(_testRoot, "source");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, content);
        var fileSystem = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var metadata = await fileSystem.GetFileMetadataAsync(path);
        return new TaggedFile("default", directory, metadata with { Hash = await fileSystem.ComputeFileHashAsync(path) })
        { UniqueFileId = name };
    }

    private BackupService CreateServiceWithState(BackupStateService state)
    {
        var source = Path.Combine(_testRoot, "source");
        Directory.CreateDirectory(source);
        return new BackupService(NullLogger<BackupService>.Instance, _telemetry, _mockApiClient.Object,
            state, new BackupScanner(new FileSystemService(NullLogger<FileSystemService>.Instance), state,
                NullLogger<BackupScanner>.Instance), _testUploader,
            Options.Create(new BackupClientOptions
            {
                BackupTargets = new() { ["default"] = source }, CommitStatusTimeoutSeconds = 0
            }));
    }

    private static Task StageRunAsync(BackupStateService state, Guid device, Guid run, Guid? commit)
    {
        var grant = new SasUrlInfo { Url = new Uri("https://storage.example.test/backups?sig=test"),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), TtlMinutes = 60 };
        return state.SavePendingBackupRunAsync(new PendingBackupRun
        {
            DeviceId = device, RunId = run, CommitId = commit, ManifestUploaded = true,
            StartedAt = DateTimeOffset.UtcNow, UploadSasUrlInfo = grant, ManifestSasUrlInfo = grant
        });
    }
}
