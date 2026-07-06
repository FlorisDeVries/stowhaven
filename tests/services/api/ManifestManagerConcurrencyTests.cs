using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
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
    private readonly ManifestManager _service;

    public ManifestManagerConcurrencyTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"manifest-manager-tests-{Guid.NewGuid():N}.db");
        var store = new SqliteStateDocumentStore(_databasePath, StateDocumentStoreExtensions.SerializerOptions);

        _service = new ManifestManager(
            store,
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
}
