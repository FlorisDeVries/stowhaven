using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

namespace FlorisDeV.BackupClient.Tests.Services;

public partial class BackupStateServiceTests
{
    [Fact]
    public async Task LocalCompletion_PersistsFilesTotalsAndIdentityAndClearsPendingRun()
    {
        var device = (await _sut.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid();
        var commit = Guid.NewGuid();
        await _sut.SavePendingBackupRunAsync(CreatePendingRun(device, run) with
        {
            ManifestUploaded = true, CommitId = commit
        });
        await _sut.AppendPendingRunFilesAsync(device, run, [CreateJournalFile("accepted.txt", size: 17)]);
        IBackupStateService state = _sut;

        Assert.Equal(LocalCompletionResult.Completed, await state.CompletePendingRunAsync(device, run, commit));

        using var reopened = ReopenState();
        var completed = await reopened.GetOrCreateDeviceStateAsync();
        Assert.Equal(run, completed.LastRunId);
        Assert.Equal(commit.ToString("N"), completed.LastCommitId);
        Assert.NotNull(completed.LastSuccessfulBackup);
        Assert.Equal(1, completed.TotalFilesTracked);
        Assert.Equal(17, completed.TotalBytesTracked);
        Assert.Equal("hash-1", (await reopened.GetFileStateAsync("documents/accepted.txt"))!.Sha256Hash);
        Assert.Null(await reopened.GetPendingBackupRunAsync(device));
        Assert.Empty(await CollectAsync(reopened.StreamPendingRunFilesAsync(device, run)));
        Assert.Empty(await CollectAsync(reopened.StreamPendingRunDeletionsAsync(device, run)));
    }

    private BackupStateService ReopenState() => new(
        Options.Create(new DatabaseOptions { FilePath = _testDatabasePath }),
        NullLogger<BackupStateService>.Instance);

    [Fact]
    public async Task LocalCompletion_ReplayPreservesCompletedStateAndUnrelatedPendingRun()
    {
        var device = (await _sut.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid();
        var commit = await StageCompletionAsync(device, run, [CreateJournalFile()]);
        await _sut.CompletePendingRunAsync(device, run, commit);
        var completed = await _sut.GetOrCreateDeviceStateAsync();
        var file = await _sut.GetFileStateAsync("documents/file.txt");
        var otherRun = Guid.NewGuid();
        await StageCompletionAsync(device, otherRun, [CreateJournalFile("next.txt")]);

        Assert.Equal(LocalCompletionResult.AlreadyCompleted, await _sut.CompletePendingRunAsync(device, run, commit));

        using var reopened = ReopenState();
        Assert.Equal(completed, await reopened.GetOrCreateDeviceStateAsync());
        Assert.Equal(file, await reopened.GetFileStateAsync("documents/file.txt"));
        Assert.Equal(otherRun, (await reopened.GetPendingBackupRunAsync(device))!.RunId);
        Assert.Single(await CollectAsync(reopened.StreamPendingRunFilesAsync(device, otherRun)));
    }

    private async Task<Guid> StageCompletionAsync(Guid device, Guid run, IReadOnlyList<TaggedFile> files)
    {
        var commit = Guid.NewGuid();
        await _sut.SavePendingBackupRunAsync(CreatePendingRun(device, run) with
        {
            ManifestUploaded = true, CommitId = commit
        });
        await _sut.AppendPendingRunFilesAsync(device, run, files);
        return commit;
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("cleanup")]
    public async Task LocalCompletion_FailureRollsBackEveryChangeAndCanBeRetried(string failurePoint)
    {
        var device = (await _sut.GetOrCreateDeviceStateAsync()).DeviceId;
        var previousRun = Guid.NewGuid();
        var previousCommit = await StageCompletionAsync(device, previousRun,
            [CreateJournalFile("changed.txt", size: 5), CreateJournalFile("deleted.txt", size: 7)]);
        await _sut.CompletePendingRunAsync(device, previousRun, previousCommit);
        var before = await _sut.GetOrCreateDeviceStateAsync();
        var run = Guid.NewGuid();
        var commit = await StageCompletionAsync(device, run, [CreateJournalFile("changed.txt", "new", 19)]);
        await _sut.BeginScanAsync();
        await _sut.AppendScannedPathsAsync(["documents/changed.txt"]);
        Assert.Equal(1, await _sut.RecordScanDeletionsAsync(device, run));
        var triggerEvent = failurePoint == "metadata"
            ? "BEFORE UPDATE OF LastRunId ON DeviceState" : "BEFORE DELETE ON PendingBackupRuns";
        await ExecuteDatabaseAsync($"""
            CREATE TRIGGER fail_completion {triggerEvent}
            BEGIN SELECT RAISE(ABORT, 'test completion failure'); END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => _sut.CompletePendingRunAsync(device, run, commit));

        using (var reopened = ReopenState())
        {
            Assert.Equal(before, await reopened.GetOrCreateDeviceStateAsync());
            Assert.Equal("hash-1", (await reopened.GetFileStateAsync("documents/changed.txt"))!.Sha256Hash);
            Assert.NotNull(await reopened.GetFileStateAsync("documents/deleted.txt"));
            Assert.Equal(run, (await reopened.GetPendingBackupRunAsync(device))!.RunId);
            Assert.Single(await CollectAsync(reopened.StreamPendingRunFilesAsync(device, run)));
            Assert.Single(await CollectAsync(reopened.StreamPendingRunDeletionsAsync(device, run)));
        }
        await ExecuteDatabaseAsync("DROP TRIGGER fail_completion");

        Assert.Equal(LocalCompletionResult.Completed, await _sut.CompletePendingRunAsync(device, run, commit));
        using var completed = ReopenState();
        Assert.Equal("new", (await completed.GetFileStateAsync("documents/changed.txt"))!.Sha256Hash);
        Assert.Null(await completed.GetFileStateAsync("documents/deleted.txt"));
        Assert.Equal(19, (await completed.GetOrCreateDeviceStateAsync()).TotalBytesTracked);
        Assert.Null(await completed.GetPendingBackupRunAsync(device));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("commit")]
    [InlineData("device")]
    public async Task LocalCompletion_InvalidIdentityLeavesStateAndPendingRunUntouched(string mismatch)
    {
        var device = (await _sut.GetOrCreateDeviceStateAsync()).DeviceId;
        var oldRun = Guid.NewGuid();
        var oldCommit = await StageCompletionAsync(device, oldRun, []);
        await _sut.CompletePendingRunAsync(device, oldRun, oldCommit);
        var run = Guid.NewGuid();
        var commit = await StageCompletionAsync(device, run, []);
        await _sut.CompletePendingRunAsync(device, run, commit);
        var before = await _sut.GetOrCreateDeviceStateAsync();
        var pendingRun = Guid.NewGuid();
        await StageCompletionAsync(device, pendingRun, [CreateJournalFile()]);
        var requestedRun = mismatch == "stale" ? oldRun : mismatch == "unknown" ? Guid.NewGuid() : pendingRun;
        var requestedCommit = mismatch == "stale" ? oldCommit : Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.CompletePendingRunAsync(
            mismatch == "device" ? Guid.NewGuid() : device, requestedRun, requestedCommit));

        using var reopened = ReopenState();
        Assert.Equal(before, await reopened.GetOrCreateDeviceStateAsync());
        Assert.Equal(pendingRun, (await reopened.GetPendingBackupRunAsync(device))!.RunId);
        Assert.Single(await CollectAsync(reopened.StreamPendingRunFilesAsync(device, pendingRun)));
    }

    [Fact]
    public async Task LocalCompletion_MissingPendingRunDoesNotInventCompletion()
    {
        var before = await _sut.GetOrCreateDeviceStateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CompletePendingRunAsync(before.DeviceId, Guid.NewGuid(), Guid.NewGuid()));
        using var reopened = ReopenState();
        Assert.Equal(before, await reopened.GetOrCreateDeviceStateAsync());
        Assert.Null(await reopened.GetPendingBackupRunAsync(before.DeviceId));
    }

    [Theory]
    [InlineData("DeviceId")]
    [InlineData("RunId")]
    public async Task LocalCompletion_RejectsStoredHeaderIdentityMismatch(string column)
    {
        var device = (await _sut.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid();
        var commit = await StageCompletionAsync(device, run, [CreateJournalFile()]);
        // Corrupt only the payload; the SQL row still identifies the original run.
        var property = column == "DeviceId" ? "deviceId" : "runId";
        await ExecuteDatabaseAsync($"UPDATE PendingBackupRuns SET PayloadJson = json_set(PayloadJson, '$.{property}', '{Guid.NewGuid()}')");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.CompletePendingRunAsync(device, run, commit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.GetPendingBackupRunAsync(device));

        Assert.Empty(await _sut.GetAllFileStatesAsync());
        Assert.Single(await CollectAsync(_sut.StreamPendingRunFilesAsync(device, run)));
        Assert.Null((await _sut.GetOrCreateDeviceStateAsync()).LastRunId);
    }

    [Fact]
    public async Task LocalCompletion_EmptyAndDeletionOnlyRunsAreValid()
    {
        var device = (await _sut.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid();
        var commit = await StageCompletionAsync(device, run, [CreateJournalFile()]);
        await _sut.CompletePendingRunAsync(device, run, commit);
        run = Guid.NewGuid();
        commit = await StageCompletionAsync(device, run, []);
        await _sut.BeginScanAsync();
        Assert.Equal(1, await _sut.RecordScanDeletionsAsync(device, run));
        await _sut.CompletePendingRunAsync(device, run, commit);
        Assert.Empty(await _sut.GetAllFileStatesAsync());
        Assert.Equal(0, (await _sut.GetOrCreateDeviceStateAsync()).TotalFilesTracked);
        run = Guid.NewGuid();
        commit = await StageCompletionAsync(device, run, []);
        await _sut.CompletePendingRunAsync(device, run, commit);
        using var reopened = ReopenState();
        Assert.Equal(run, (await reopened.GetOrCreateDeviceStateAsync()).LastRunId);
        Assert.Null(await reopened.GetPendingBackupRunAsync(device));
    }

    private async Task ExecuteDatabaseAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_testDatabasePath}");
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalCompletion_CancellationRespectsTheCommitBoundary(bool cancelAtCommit)
    {
        var device = (await _sut.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid();
        var commit = await StageCompletionAsync(device, run, [CreateJournalFile()]);
        using var cancellation = new CancellationTokenSource();
        // Install native hooks on the pooled connection used by this private database.
        // They observe real SQL execution without a production failure-injection port.
        await using var hookConnection = new SqliteConnection($"Data Source={_testDatabasePath}");
        await hookConnection.OpenAsync();
        var handle = hookConnection.Handle!;
        var observed = false;
        if (cancelAtCommit)
            SQLitePCL.raw.sqlite3_commit_hook(handle, _ => { observed = true; cancellation.Cancel(); return 0; }, null);
        else
            SQLitePCL.raw.sqlite3_update_hook(handle, (_, _, _, table, _) =>
            {
                if (table == "DeviceState") { observed = true; cancellation.Cancel(); }
            }, null);
        await hookConnection.CloseAsync();
        try
        {
            if (cancelAtCommit)
                Assert.Equal(LocalCompletionResult.Completed,
                    await _sut.CompletePendingRunAsync(device, run, commit, cancellation.Token));
            else
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => _sut.CompletePendingRunAsync(device, run, commit, cancellation.Token));
            Assert.True(observed);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_commit_hook(handle, null, null);
            SQLitePCL.raw.sqlite3_update_hook(handle, (SQLitePCL.delegate_update)null!, null);
        }

        using var reopened = ReopenState();
        var completed = await reopened.GetOrCreateDeviceStateAsync();
        Assert.Equal(cancelAtCommit ? run : (Guid?)null, completed.LastRunId);
        Assert.Equal(cancelAtCommit ? 1 : 0, completed.TotalFilesTracked);
        Assert.Equal(!cancelAtCommit, await reopened.GetPendingBackupRunAsync(device) is not null);
        Assert.Equal(cancelAtCommit, await reopened.GetFileStateAsync("documents/file.txt") is not null);
    }

    [Fact]
    public async Task LocalCompletion_CancelWhileWaitingForStateLockRetainsPendingRun()
    {
        var device = (await _sut.GetOrCreateDeviceStateAsync()).DeviceId;
        var run = Guid.NewGuid();
        var commit = await StageCompletionAsync(device, run, [CreateJournalFile()]);
        await using (var journal = _sut.StreamPendingRunFilesAsync(device, run).GetAsyncEnumerator())
        {
            Assert.True(await journal.MoveNextAsync());
            using var cancellation = new CancellationTokenSource();
            var completion = _sut.CompletePendingRunAsync(device, run, commit, cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        }
        using var reopened = ReopenState();
        Assert.Equal(run, (await reopened.GetPendingBackupRunAsync(device))!.RunId);
        Assert.Empty(await reopened.GetAllFileStatesAsync());
        Assert.Null((await reopened.GetOrCreateDeviceStateAsync()).LastRunId);
    }

    private async Task CompleteJournalAsync(Guid device, Guid run)
    {
        var commit = Guid.NewGuid();
        var pending = await _sut.GetPendingBackupRunAsync(device);
        await _sut.SavePendingBackupRunAsync(pending! with { ManifestUploaded = true, CommitId = commit });
        await _sut.CompletePendingRunAsync(device, run, commit);
    }

    private async Task SeedTrackedStateAsync(Guid run, string commit, IReadOnlyList<FileMetadata> files)
    {
        var device = (await _sut.GetOrCreateDeviceStateAsync()).DeviceId;
        await _sut.SavePendingBackupRunAsync(CreatePendingRun(device, run) with
        {
            ManifestUploaded = true, CommitId = Guid.Parse(commit)
        });
        var staged = files.Select(file =>
        {
            var separator = file.FilePath.IndexOf('/');
            return new TaggedFile(file.FilePath[..separator], "/source",
                file with { FilePath = "/source/" + file.FilePath[(separator + 1)..] });
        }).ToArray();
        await _sut.AppendPendingRunFilesAsync(device, run, staged);
        await _sut.CompletePendingRunAsync(device, run, Guid.Parse(commit));
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source) result.Add(item);
        return result;
    }
}
