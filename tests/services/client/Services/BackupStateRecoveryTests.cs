using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FlorisDeV.BackupClient.Tests.Services;

public partial class BackupStateServiceTests
{
    [Fact]
    public async Task CaseDistinctPaths_SurviveJournalPromotionAndDeletionDetection()
    {
        if (OperatingSystem.IsWindows()) return;
        var device = Guid.NewGuid(); var run = Guid.NewGuid();
        var first = CreateJournalFile("Readme.txt", "hash-first", 1, "first");
        var second = CreateJournalFile("README.txt", "hash-second", 2, "second");
        await _sut.AppendPendingRunFilesAsync(device, run, [first, second]);
        await _sut.PromotePendingRunFilesToStateAsync(device, run);
        Assert.Equal("hash-first", (await _sut.GetFileStateAsync(first.GetStoragePath()))!.Sha256Hash);
        Assert.Equal("hash-second", (await _sut.GetFileStateAsync(second.GetStoragePath()))!.Sha256Hash);
        await _sut.BeginScanAsync();
        await _sut.AppendScannedPathsAsync([first.GetStoragePath()]);
        Assert.Equal(1, await _sut.RecordScanDeletionsAsync(device, run));
        await _sut.ApplyPendingRunDeletionsAsync(device, run);
        Assert.NotNull(await _sut.GetFileStateAsync(first.GetStoragePath()));
        Assert.Null(await _sut.GetFileStateAsync(second.GetStoragePath()));
    }

    [Fact]
    public async Task VersionTwoMigration_PreservesSubmittedRunAndChangesPathCollation()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid(); var commit = Guid.NewGuid();
        await _sut.SavePendingBackupRunAsync(CreatePendingRun(device, run) with { ManifestUploaded = true, CommitId = commit });
        await _sut.AppendPendingRunFilesAsync(device, run, [CreateJournalFile("Readme.txt")]);
        await _sut.PromotePendingRunFilesToStateAsync(device, run);
        await using (var connection = new SqliteConnection($"Data Source={_testDatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE PendingRunFiles RENAME TO PendingRunFilesOld;
                CREATE TABLE PendingRunFiles (
                    DeviceId TEXT NOT NULL, RunId TEXT NOT NULL,
                    StoragePath TEXT NOT NULL COLLATE NOCASE,
                    Sha256Hash TEXT NOT NULL, SizeBytes INTEGER NOT NULL,
                    LastModifiedUtc TEXT NOT NULL, UniqueFileId TEXT, PayloadJson TEXT NOT NULL,
                    PRIMARY KEY (DeviceId, RunId, StoragePath));
                INSERT INTO PendingRunFiles SELECT * FROM PendingRunFilesOld;
                DROP TABLE PendingRunFilesOld;
                PRAGMA user_version = 2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var upgraded = new BackupStateService(Options.Create(new DatabaseOptions { FilePath = _testDatabasePath }),
            NullLogger<BackupStateService>.Instance);
        var header = await upgraded.GetPendingBackupRunAsync(device);
        Assert.NotNull(header);
        Assert.True(header.ManifestUploaded);
        Assert.Equal(commit, header.CommitId);
        Assert.Equal(1, header.UploadedFileCount);
        Assert.Single(await upgraded.GetAllFileStatesAsync());
        await upgraded.AppendPendingRunFilesAsync(device, run, [CreateJournalFile("README.txt", "other-hash")]);
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, (await upgraded.GetPendingBackupRunAsync(device))!.UploadedFileCount);
    }

    [Fact]
    public async Task RecoverFailedRun_InvalidatesOnlyAffectedPathsAndClearsJournal()
    {
        var device = Guid.NewGuid(); var run = Guid.NewGuid();
        var changed = CreateJournalFile("changed.txt");
        var deleted = CreateJournalFile("deleted.txt");
        var newlyPublished = CreateJournalFile("newly-published.txt");
        var untouched = CreateJournalFile("untouched.txt");
        await _sut.SaveBackupSuccessAsync(Guid.NewGuid(), "previous", [
            changed.Metadata with { FilePath = changed.GetStoragePath() },
            deleted.Metadata with { FilePath = deleted.GetStoragePath() },
            untouched.Metadata with { FilePath = untouched.GetStoragePath() }]);
        await _sut.SavePendingBackupRunAsync(CreatePendingRun(device, run));
        await _sut.AppendPendingRunFilesAsync(device, run, [changed, newlyPublished]);
        await _sut.BeginScanAsync();
        await _sut.AppendScannedPathsAsync([changed.GetStoragePath(), untouched.GetStoragePath()]);
        await _sut.RecordScanDeletionsAsync(device, run);

        await _sut.RecoverFailedRunAsync(device, run);

        Assert.Equal("", (await _sut.GetFileStateAsync(changed.GetStoragePath()))!.Sha256Hash);
        Assert.Equal("", (await _sut.GetFileStateAsync(deleted.GetStoragePath()))!.Sha256Hash);
        Assert.Equal("", (await _sut.GetFileStateAsync(newlyPublished.GetStoragePath()))!.Sha256Hash);
        var fs = new Mock<IFileSystemService>();
        fs.Setup(x => x.ComputeFileHashAsync(changed.Metadata.FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(changed.Metadata.Hash!);
        var scanner = new BackupScanner(fs.Object, _sut, NullLogger<BackupScanner>.Instance);
        Assert.True((await scanner.AnalyzeFileAsync(changed, CancellationToken.None)).NeedsBackup);
        Assert.NotNull(await _sut.GetFileStateAsync(untouched.GetStoragePath()));
        Assert.Null(await _sut.GetPendingBackupRunAsync(device));
        await _sut.RecoverFailedRunAsync(device, run); // Repeating cleanup is safe.
        Assert.Equal(4, (await _sut.GetAllFileStatesAsync()).Count);
        await _sut.BeginScanAsync();
        await _sut.AppendScannedPathsAsync([untouched.GetStoragePath()]);
        Assert.Equal(3, await _sut.CountScanDeletionsAsync());
    }
}
