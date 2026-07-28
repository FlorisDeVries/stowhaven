using Azure;
using Azure.Storage.Blobs;
using FluentAssertions;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Paging tests for the operations manifest endpoints, against the real SQLite-backed document store
/// so chunk layout and continuation tokens are exercised end to end. These endpoints must never
/// materialize a whole manifest: a run can cover hundreds of thousands of files.
/// </summary>
public sealed class OperationalServiceManifestPagingTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteStateDocumentStore _store;
    private readonly ManifestManager _manifestManager;
    private readonly OperationalService _sut;

    public OperationalServiceManifestPagingTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"ops-manifest-paging-{Guid.NewGuid():N}.db");
        _store = new SqliteStateDocumentStore(_databasePath, StateDocumentStoreExtensions.SerializerOptions);

        _manifestManager = new ManifestManager(
            _store,
            new Mock<ILogger<ManifestManager>>().Object,
            new Mock<TelemetryProvider>().Object);

        // No manifest blobs exist in these tests, so the blob fallback always reports "absent" and
        // the state store is the only source.
        var manifestBlob = new Mock<BlobClient>();
        manifestBlob
            .Setup(x => x.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));

        var container = new Mock<BlobContainerClient>();
        container.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(manifestBlob.Object);

        var blobStorage = new Mock<IBlobStorageService>();
        blobStorage
            .Setup(x => x.GetContainerClientAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(container.Object);

        _sut = new OperationalService(
            _manifestManager,
            new Mock<IBackupEventPublisher>().Object,
            blobStorage.Object,
            new Mock<ILogger<OperationalService>>().Object);
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

    private async Task<(Guid DeviceId, Guid RunId)> ArrangeRunAsync(int fileCount, int deletedCount)
    {
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await _manifestManager.CreateBackupRunAsync(deviceId, runId, DateTimeOffset.UtcNow);

        await _manifestManager.SaveRunManifestAsync(deviceId, runId, new RunManifest
        {
            DeviceId = $"{deviceId:N}",
            RunId = $"{runId:N}",
            Files = Enumerable.Range(0, fileCount).Select(i => new ManifestFileEntry
            {
                RelativePath = $"docs/file-{i:D5}.txt",
                UniqueFileId = $"file-{i:D5}",
                Sha256 = new string('a', 64),
                Size = i,
                Mtime = DateTimeOffset.UnixEpoch.AddSeconds(i)
            }).ToList(),
            Deleted = Enumerable.Range(0, deletedCount).Select(i => $"old/removed-{i:D5}.txt").ToList()
        });

        return (deviceId, runId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListManifestFiles_ReturnsEveryEntryExactlyOnceAcrossPages()
    {
        // Arrange: spans several 500-entry chunks, with a page size that does not divide the chunk
        // size so pages repeatedly end mid-chunk.
        var (deviceId, runId) = await ArrangeRunAsync(fileCount: 1250, deletedCount: 600);

        var files = new List<string>();
        var deleted = new List<string>();
        string? token = null;
        var pages = 0;

        // Act
        do
        {
            var page = await _sut.ListManifestFilesAsync(deviceId, runId, pageSize: 300, continuationToken: token);
            pages++;

            (page.Files.Count + page.Deleted.Count).Should().BeLessThanOrEqualTo(300);
            page.FileCount.Should().Be(1250);
            page.DeletedCount.Should().Be(600);
            page.PageSize.Should().Be(300);

            files.AddRange(page.Files.Select(f => f.UniqueFileId));
            deleted.AddRange(page.Deleted);

            token = page.NextContinuationToken;
            pages.Should().BeLessThan(50, "paging should terminate");
        }
        while (token != null);

        // Assert: no gaps, no duplicates, original order preserved.
        pages.Should().BeGreaterThan(1);
        files.Should().HaveCount(1250);
        files.Should().OnlyHaveUniqueItems();
        files[0].Should().Be("file-00000");
        files[^1].Should().Be("file-01249");
        deleted.Should().HaveCount(600);
        deleted.Should().OnlyHaveUniqueItems();
        deleted[0].Should().Be("old/removed-00000.txt");
        deleted[^1].Should().Be("old/removed-00599.txt");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListManifestFiles_WhenPageSizeExceedsMaximum_IsClamped()
    {
        // Arrange: a response must not be able to grow unbounded on request.
        var (deviceId, runId) = await ArrangeRunAsync(fileCount: 1200, deletedCount: 0);

        // Act
        var page = await _sut.ListManifestFilesAsync(deviceId, runId, pageSize: 100_000);

        // Assert
        page.PageSize.Should().Be(1000);
        page.Files.Should().HaveCount(1000);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListManifestFiles_WhenRunFitsInOnePage_ReportsNoMore()
    {
        // Arrange
        var (deviceId, runId) = await ArrangeRunAsync(fileCount: 3, deletedCount: 2);

        // Act
        var page = await _sut.ListManifestFilesAsync(deviceId, runId, pageSize: 100);

        // Assert
        page.Files.Should().HaveCount(3);
        page.Deleted.Should().HaveCount(2);
        page.HasMore.Should().BeFalse();
        page.NextContinuationToken.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListManifestFiles_WhenContinuationTokenIsMalformed_Throws()
    {
        // Arrange
        var (deviceId, runId) = await ArrangeRunAsync(fileCount: 5, deletedCount: 0);

        // Act
        var act = async () => await _sut.ListManifestFilesAsync(
            deviceId, runId, pageSize: 10, continuationToken: "not-a-real-token");

        // Assert: an opaque token the caller mangled is a client error, not a server fault.
        await act.Should().ThrowAsync<InvalidContinuationTokenException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListManifestFiles_WhenManifestNotAvailable_Throws()
    {
        // Arrange: a run with no persisted manifest and no blob fallback.
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _manifestManager.CreateBackupRunAsync(deviceId, runId, DateTimeOffset.UtcNow);

        // Act
        var act = async () => await _sut.ListManifestFilesAsync(deviceId, runId, pageSize: 10);

        // Assert
        await act.Should().ThrowAsync<ManifestPayloadNotAvailableException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetManifestDetails_ReportsCountsAndPointsAtFilesRouteInsteadOfInliningEntries()
    {
        // Arrange
        var (deviceId, runId) = await ArrangeRunAsync(fileCount: 1250, deletedCount: 600);

        // Act
        var details = await _sut.GetManifestDetailsAsync(deviceId, runId);

        // Assert: the payload is deliberately absent; only totals and a pointer are returned.
        details.ManifestAvailable.Should().BeTrue();
        details.ManifestUnavailableReason.Should().BeNull();
        details.FileCount.Should().Be(1250);
        details.DeletedCount.Should().Be(600);
        details.FilesUrl.Should().Be($"/api/ops/manifests/{deviceId:D}/{runId:D}/files");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetManifestDetails_WhenManifestNotAvailable_SaysSo()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _manifestManager.CreateBackupRunAsync(deviceId, runId, DateTimeOffset.UtcNow);

        // Act
        var details = await _sut.GetManifestDetailsAsync(deviceId, runId);

        // Assert
        details.ManifestAvailable.Should().BeFalse();
        details.ManifestUnavailableReason.Should().NotBeNull();
        details.FileCount.Should().BeNull();
        details.FilesUrl.Should().BeNull();
    }
}
