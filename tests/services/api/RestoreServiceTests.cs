using FluentAssertions;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.State;
using Moq;

namespace FlorisDeV.BackupApi.Tests;

public class RestoreServiceTests
{
    private readonly Mock<IManifestManager> _manifestManager = new();
    private readonly Mock<ISasUrlService> _sasUrlService = new();
    private readonly RestoreService _sut;

    public RestoreServiceTests()
    {
        _sut = new RestoreService(_manifestManager.Object, _sasUrlService.Object);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListRestoreFilesAsync_ReturnsOnlyActiveNonDeletedFiles()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var activeEntry = CreateFileEntry(deviceId, "documents/a.txt", "version-a", isDeleted: false);
        var deletedEntry = CreateFileEntry(deviceId, "documents/deleted.txt", "version-deleted", isDeleted: true);
        var activeVersion = CreateFileVersion(deviceId, "version-a", "documents/a.txt", FileVersionState.Active);

        _manifestManager.Setup(x => x.GetAllFileEntriesAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([activeEntry, deletedEntry]);
        _manifestManager.Setup(x => x.GetFileEntriesPageAsync(deviceId, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileEntryPage
            {
                Entries = [activeEntry, deletedEntry],
                PageSize = 100,
                ContinuationToken = null,
                NextContinuationToken = "next-page"
            });
        _manifestManager.Setup(x => x.GetFileVersionAsync(deviceId, "version-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeVersion);

        // Act
        var result = await _sut.ListRestoreFilesAsync(deviceId, cancellationToken: CancellationToken.None);

        // Assert
        result.DeviceId.Should().Be(deviceId);
        result.PageSize.Should().Be(100);
        result.NextContinuationToken.Should().Be("next-page");
        result.HasMore.Should().BeTrue();
        result.Files.Should().ContainSingle();
        result.Files[0].LogicalPath.Should().Be("documents/a.txt");
        result.Files[0].UniqueFileId.Should().Be("version-a");
        _manifestManager.Verify(x => x.GetFileVersionAsync(deviceId, "version-deleted", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListRestoreFilesAsync_WithPageParameters_UsesPagedStateLookup()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var entry = CreateFileEntry(deviceId, "documents/b.txt", "version-b", isDeleted: false);
        var version = CreateFileVersion(deviceId, "version-b", "documents/b.txt", FileVersionState.Active);

        _manifestManager.Setup(x => x.GetFileEntriesPageAsync(deviceId, 25, "cursor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileEntryPage
            {
                Entries = [entry],
                PageSize = 25,
                ContinuationToken = "cursor",
                NextContinuationToken = null
            });
        _manifestManager.Setup(x => x.GetFileVersionAsync(deviceId, "version-b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(version);

        // Act
        var result = await _sut.ListRestoreFilesAsync(deviceId, pageSize: 25, continuationToken: "cursor", cancellationToken: CancellationToken.None);

        // Assert
        result.PageSize.Should().Be(25);
        result.ContinuationToken.Should().Be("cursor");
        result.NextContinuationToken.Should().BeNull();
        result.HasMore.Should().BeFalse();
        result.Files.Should().ContainSingle(f => f.LogicalPath == "documents/b.txt");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartRestoreAsync_WithValidSelection_ReturnsReadSasAndFileMetadata()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var entry = CreateFileEntry(deviceId, "documents/a.txt", "version-a", isDeleted: false);
        var version = CreateFileVersion(deviceId, "version-a", "documents/a.txt", FileVersionState.Active);
        var sas = new SasUrlInfo
        {
            Url = new Uri("https://storage.example/backups/devices/files?sas=1"),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            TtlMinutes = 60,
            BasePath = $"devices/{deviceId:N}/files",
            IsPathEmbedded = false
        };

        _manifestManager.Setup(x => x.GetFileEntryAsync(deviceId, "documents/a.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);
        _manifestManager.Setup(x => x.GetFileVersionAsync(deviceId, "version-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(version);
        _sasUrlService.Setup(x => x.GenerateReadSasUrlAsync($"devices/{deviceId:N}/files", "127.0.0.1", 60, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sas);

        // Act
        var result = await _sut.StartRestoreAsync(deviceId, new StartRestoreRequest
        {
            LogicalPaths = ["documents/a.txt"]
        }, "127.0.0.1", CancellationToken.None);

        // Assert
        result.DeviceId.Should().Be(deviceId);
        result.SasUrlInfo.Should().BeSameAs(sas);
        result.Files.Should().ContainSingle();
        result.Files[0].LogicalPath.Should().Be("documents/a.txt");
        result.Files[0].Sha256.Should().Be(version.Sha256);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StartRestoreAsync_WhenFileIsDeleted_ThrowsFileNotFoundException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        _manifestManager.Setup(x => x.GetFileEntryAsync(deviceId, "documents/deleted.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateFileEntry(deviceId, "documents/deleted.txt", "version-deleted", isDeleted: true));

        // Act
        var act = async () => await _sut.StartRestoreAsync(deviceId, new StartRestoreRequest
        {
            LogicalPaths = ["documents/deleted.txt"]
        }, cancellationToken: CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    private static FileEntry CreateFileEntry(Guid deviceId, string logicalPath, string versionId, bool isDeleted) => new()
    {
        DeviceId = deviceId,
        RelativePath = logicalPath,
        CurrentVersionId = versionId,
        Size = 100,
        LastWriteUtc = DateTimeOffset.UtcNow,
        LastBackupRunId = Guid.NewGuid().ToString("N"),
        IsDeleted = isDeleted
    };

    private static FileVersion CreateFileVersion(Guid deviceId, string versionId, string logicalPath, FileVersionState state) => new()
    {
        DeviceId = deviceId,
        UniqueFileId = versionId,
        RelativePath = logicalPath,
        Sha256 = "abc123",
        Size = 100,
        CreatedAt = DateTimeOffset.UtcNow,
        State = state
    };
}
