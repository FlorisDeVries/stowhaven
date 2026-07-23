using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Concurrency and lifecycle tests for <see cref="ManifestManager"/> running against the
/// SQLite <see cref="IStateDocumentStore"/> backend so ETag semantics are exercised for real.
/// </summary>
public sealed class ManifestManagerConcurrencyTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteStateDocumentStore _store;
    private readonly ManifestManager _service;

    public ManifestManagerConcurrencyTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"manifest-manager-tests-{Guid.NewGuid():N}.db");
        _store = new SqliteStateDocumentStore(_databasePath, StateDocumentStoreExtensions.SerializerOptions);

        _service = new ManifestManager(
            _store,
            new Mock<ILogger<ManifestManager>>().Object,
            new Mock<TelemetryProvider>().Object);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_QueuedRun_SuccessfullyCommits()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _service.CreateBackupRunAsync(deviceId, runId, DateTimeOffset.UtcNow);

        var result = await _service.CommitBackupRunAsync(deviceId, runId);

        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.NotNull(result.CompletedAt);

        var persisted = await _service.GetBackupRunAsync(deviceId, runId);
        Assert.Equal(BackupRunStatus.Succeeded, persisted.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateBackupRunAsync_WithStaleETag_ThrowsConcurrentUpdateException()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _service.CreateBackupRunAsync(deviceId, runId, DateTimeOffset.UtcNow);

        // Two readers hold the same version; the first write wins, the second must conflict.
        var firstReader = await _service.GetBackupRunAsync(deviceId, runId);
        var secondReader = await _service.GetBackupRunAsync(deviceId, runId);

        firstReader.Status = BackupRunStatus.Processing;
        await _service.UpdateBackupRunAsync(deviceId, runId, firstReader);

        secondReader.Status = BackupRunStatus.Failed;
        var exception = await Assert.ThrowsAsync<ConcurrentUpdateException>(
            () => _service.UpdateBackupRunAsync(deviceId, runId, secondReader));

        Assert.Equal(deviceId, exception.DeviceId);
        Assert.Equal(runId, exception.RunId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_AlreadyCommitted_ThrowsBackupRunAlreadyCommittedException()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _service.CreateBackupRunAsync(deviceId, runId, DateTimeOffset.UtcNow);
        await _service.CommitBackupRunAsync(deviceId, runId);

        var exception = await Assert.ThrowsAsync<BackupRunAlreadyCommittedException>(
            () => _service.CommitBackupRunAsync(deviceId, runId));

        Assert.Equal(deviceId, exception.DeviceId);
        Assert.Equal(runId, exception.RunId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_FailedState_ThrowsInvalidBackupRunStateException()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _service.CreateBackupRunAsync(deviceId, runId, DateTimeOffset.UtcNow);

        var run = await _service.GetBackupRunAsync(deviceId, runId);
        run.Status = BackupRunStatus.Failed;
        await _service.UpdateBackupRunAsync(deviceId, runId, run);

        var exception = await Assert.ThrowsAsync<InvalidBackupRunStateException>(
            () => _service.CommitBackupRunAsync(deviceId, runId));

        Assert.Equal(BackupRunStatus.Failed, exception.CurrentStatus);
        Assert.Equal(BackupRunStatus.Queued, exception.ExpectedStatus);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_RunNotFound_ThrowsBackupRunNotFoundException()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<BackupRunNotFoundException>(
            () => _service.CommitBackupRunAsync(deviceId, runId));

        Assert.Equal(deviceId, exception.DeviceId);
        Assert.Equal(runId, exception.RunId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetBackupRunAsync_StoresETagInModel()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _service.CreateBackupRunAsync(deviceId, runId, DateTimeOffset.UtcNow);

        var result = await _service.GetBackupRunAsync(deviceId, runId);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.ETag));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetBackupRunAsync_NotFound_ThrowsBackupRunNotFoundException()
    {
        await Assert.ThrowsAsync<BackupRunNotFoundException>(
            () => _service.GetBackupRunAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TryClaimCommitJobAsync_SecondClaim_IsRejected()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitJob = await _service.CreateCommitJobAsync(deviceId, runId);

        var (claimedFirst, _) = await _service.TryClaimCommitJobAsync(commitJob.CommitId);
        var (claimedSecond, second) = await _service.TryClaimCommitJobAsync(commitJob.CommitId);

        Assert.True(claimedFirst);
        Assert.False(claimedSecond);
        Assert.Equal(CommitJobStatus.Processing, second.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCommitFileProgressPageAsync_PagesOrderedByFileId()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var commitJob = await _service.CreateCommitJobAsync(deviceId, runId);

        for (var i = 0; i < 5; i++)
        {
            await _service.SaveCommitFileProgressAsync(new CommitFileProgress
            {
                CommitId = commitJob.CommitId,
                DeviceId = deviceId,
                RunId = runId,
                UniqueFileId = $"file-{i:D3}",
                LogicalPath = $"docs/file-{i:D3}.txt",
                Status = CommitFileStatus.Succeeded
            });
        }

        var firstPage = await _service.GetCommitFileProgressPageAsync(commitJob.CommitId, pageSize: 3);
        Assert.Equal(3, firstPage.Files.Count);
        Assert.True(firstPage.HasMore);
        Assert.Equal(["file-000", "file-001", "file-002"], firstPage.Files.Select(f => f.UniqueFileId).ToArray());

        var secondPage = await _service.GetCommitFileProgressPageAsync(
            commitJob.CommitId, pageSize: 3, firstPage.NextContinuationToken);
        Assert.Equal(2, secondPage.Files.Count);
        Assert.False(secondPage.HasMore);
        Assert.Equal(["file-003", "file-004"], secondPage.Files.Select(f => f.UniqueFileId).ToArray());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetBackupRunsPageAsync_FiltersByDeviceAndOrdersNewestFirst()
    {
        var deviceId = Guid.NewGuid();
        var otherDeviceId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        await _service.CreateBackupRunAsync(deviceId, Guid.NewGuid(), baseTime.AddMinutes(-30));
        await _service.CreateBackupRunAsync(deviceId, Guid.NewGuid(), baseTime.AddMinutes(-10));
        await _service.CreateBackupRunAsync(otherDeviceId, Guid.NewGuid(), baseTime.AddMinutes(-20));

        var page = await _service.GetBackupRunsPageAsync(new BackupRunQuery { DeviceId = deviceId, PageSize = 10 });

        Assert.Equal(2, page.Runs.Count);
        Assert.All(page.Runs, run => Assert.Equal(deviceId, run.DeviceId));
        Assert.True(page.Runs[0].StartedAt > page.Runs[1].StartedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SaveRunManifestAsync_LargeManifest_RoundTripsAcrossChunks()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        // Well over the 500-entry chunk size for both files and deletions, so reassembly must span
        // multiple chunk documents and preserve order.
        var files = Enumerable.Range(0, 1250)
            .Select(i => new ManifestFileEntry
            {
                RelativePath = $"docs/file-{i:D5}.txt",
                UniqueFileId = $"file-{i:D5}",
                Sha256 = new string('a', 64),
                Size = i,
                Mtime = DateTimeOffset.UnixEpoch.AddSeconds(i)
            })
            .ToList();
        var deleted = Enumerable.Range(0, 600).Select(i => $"old/removed-{i:D5}.txt").ToList();

        var manifest = new RunManifest
        {
            DeviceId = $"{deviceId:N}",
            RunId = $"{runId:N}",
            Files = files,
            Deleted = deleted
        };

        await _service.SaveRunManifestAsync(deviceId, runId, manifest);

        var reassembled = await _service.GetRunManifestAsync(deviceId, runId);

        Assert.NotNull(reassembled);
        Assert.Equal(1250, reassembled.Files.Count);
        Assert.Equal(600, reassembled.Deleted.Count);
        // Order preserved across chunk boundaries.
        Assert.Equal("file-00000", reassembled.Files[0].UniqueFileId);
        Assert.Equal("file-00499", reassembled.Files[499].UniqueFileId);
        Assert.Equal("file-00500", reassembled.Files[500].UniqueFileId);
        Assert.Equal("file-01249", reassembled.Files[1249].UniqueFileId);
        Assert.Equal("old/removed-00000.txt", reassembled.Deleted[0]);
        Assert.Equal("old/removed-00599.txt", reassembled.Deleted[599]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetRunManifestAsync_LegacyInlineDocument_IsReadFromHeader()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        // Simulate a pre-chunking (v1) manifest persisted inline as a single document, using the same
        // document type/partition/id encoding ManifestManager uses internally.
        var legacy = new RunManifest
        {
            SchemaVersion = 1,
            DeviceId = $"{deviceId:N}",
            RunId = $"{runId:N}",
            Files =
            [
                new ManifestFileEntry
                {
                    RelativePath = "legacy/only.txt",
                    UniqueFileId = "legacy-file",
                    Sha256 = new string('b', 64),
                    Size = 42,
                    Mtime = DateTimeOffset.UnixEpoch
                }
            ],
            Deleted = ["legacy/gone.txt"]
        };

        await _store.UpsertAsync("runManifest", $"device:{deviceId:N}", $"{runId:N}", legacy);

        var result = await _service.GetRunManifestAsync(deviceId, runId);

        Assert.NotNull(result);
        Assert.Single(result.Files);
        Assert.Equal("legacy-file", result.Files[0].UniqueFileId);
        Assert.Equal(["legacy/gone.txt"], result.Deleted);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetRunManifestAsync_EmptyManifest_RoundTripsAsEmpty()
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await _service.SaveRunManifestAsync(deviceId, runId, new RunManifest
        {
            DeviceId = $"{deviceId:N}",
            RunId = $"{runId:N}",
            Files = [],
            Deleted = []
        });

        var result = await _service.GetRunManifestAsync(deviceId, runId);

        Assert.NotNull(result);
        Assert.Empty(result.Files);
        Assert.Empty(result.Deleted);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetRunManifestAsync_Missing_ReturnsNull()
    {
        var result = await _service.GetRunManifestAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(result);
    }
}
