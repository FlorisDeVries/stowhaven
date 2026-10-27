using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FlorisDeV.BackupClient.Tests.Services;

public class RestoreServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"restore-tests-{Guid.NewGuid():N}");
    private readonly Guid _device = Guid.NewGuid();
    private readonly Mock<IBackupApiClient> _api = new();
    private readonly Mock<IRestoreBlobClientFactory> _factory = new();
    private readonly byte[] _content = "Restored and verified content"u8.ToArray();
    private RestoreFileItem FileItem => new()
    {
        LogicalPath = "documents/file.txt", UniqueFileId = "version", Size = _content.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(_content)).ToLowerInvariant(), LastWriteUtc = DateTimeOffset.UtcNow
    };
    private SasUrlInfo Sas(bool expired = false) => new()
    {
        Url = new Uri("https://storage.test/backups?token=temporary"), BasePath = $"devices/{_device:N}/files",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 60), TtlMinutes = 60
    };
    private StartRestoreResponse Response(RestoreFileAvailability availability = RestoreFileAvailability.Ready, bool expired = false)
        => new() { DeviceId = _device, RestoreId = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            SasUrlInfo = Sas(expired), Files = [FileItem with { Availability = availability }] };

    private RestoreService Service(bool rehydrate = true, int timeoutMinutes = 1)
        => new(_api.Object, Mock.Of<IBackupStateService>(), Mock.Of<IBackupEncryptionService>(),
            Options.Create(new BackupClientOptions { BackupTargets = new(), Restore = new()
            {
                DeviceId = _device, DestinationPath = _root, LogicalPaths = [FileItem.LogicalPath],
                RehydrateArchivedFiles = rehydrate, RehydrationPollIntervalSeconds = 1, RehydrationTimeoutMinutes = timeoutMinutes
            } }), NullLogger<RestoreService>.Instance, _factory.Object);

    private Mock<BlobClient> DownloadClient(bool corrupt = false)
    {
        var blob = new Mock<BlobClient>();
        blob.Setup(x => x.DownloadToAsync(It.IsAny<string>(), It.IsAny<BlobDownloadToOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (string path, BlobDownloadToOptions options, CancellationToken token) =>
            {
                await System.IO.File.WriteAllBytesAsync(path, corrupt ? "invalid"u8.ToArray() : _content, token);
                options.ProgressHandler?.Report(_content.Length);
                return Mock.Of<Response>();
            });
        var container = new Mock<BlobContainerClient>();
        container.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        _factory.Setup(x => x.Create(It.IsAny<Uri>())).Returns(container.Object);
        return blob;
    }

    [Fact]
    public async Task RehydrationIsPolledBeforeDownloadingAndVerifyingFile()
    {
        _api.SetupSequence(x => x.StartRestore(_device, It.IsAny<StartRestoreRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(RestoreFileAvailability.Rehydrating)).ReturnsAsync(Response());
        DownloadClient();
        Assert.True(await Service().RestoreAsync());
        Assert.Equal(_content, await System.IO.File.ReadAllBytesAsync(Path.Combine(_root, "documents", "file.txt")));
        _api.Verify(x => x.StartRestore(_device, It.Is<StartRestoreRequest>(r => r.RehydrateArchivedFiles), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ArchivedFile_WithAutomaticPreparationDisabled_DoesNotDownload()
    {
        _api.Setup(x => x.StartRestore(_device, It.IsAny<StartRestoreRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Response(RestoreFileAvailability.Archived));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(rehydrate: false).RestoreAsync());
        _factory.Verify(x => x.Create(It.IsAny<Uri>()), Times.Never);
    }

    [Fact]
    public async Task RehydrationTimeout_DoesNotStartDownload()
    {
        _api.Setup(x => x.StartRestore(_device, It.IsAny<StartRestoreRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Response(RestoreFileAvailability.Rehydrating));
        await Assert.ThrowsAsync<TimeoutException>(() => Service(timeoutMinutes: 0).RestoreAsync());
        _factory.Verify(x => x.Create(It.IsAny<Uri>()), Times.Never);
    }

    [Fact]
    public async Task ExpiringCredentialsAreRenewedBeforeDownloading()
    {
        _api.Setup(x => x.StartRestore(_device, It.IsAny<StartRestoreRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Response(expired: true));
        _api.Setup(x => x.RefreshRestoreSas(_device, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefreshRestoreSasResponse { DeviceId = _device, SasUrlInfo = Sas() });
        DownloadClient();
        Assert.True(await Service().RestoreAsync());
        _api.Verify(x => x.RefreshRestoreSas(_device, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExpiryDuringDownload_RenewsAndRetriesOnce()
    {
        _api.Setup(x => x.StartRestore(_device, It.IsAny<StartRestoreRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Response());
        _api.Setup(x => x.RefreshRestoreSas(_device, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefreshRestoreSasResponse { DeviceId = _device, SasUrlInfo = Sas() });
        var blob = DownloadClient();
        var attempts = 0;
        blob.Setup(x => x.DownloadToAsync(It.IsAny<string>(), It.IsAny<BlobDownloadToOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (string path, BlobDownloadToOptions _, CancellationToken token) =>
            {
                if (++attempts == 1) throw new RequestFailedException(403, "Expired", "AuthenticationFailed", null);
                await System.IO.File.WriteAllBytesAsync(path, _content, token); return Mock.Of<Response>();
            });
        Assert.True(await Service().RestoreAsync());
        _api.Verify(x => x.RefreshRestoreSas(_device, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CorruptDownload_IsRejectedBeforePublishingDestination()
    {
        _api.Setup(x => x.StartRestore(_device, It.IsAny<StartRestoreRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Response());
        DownloadClient(corrupt: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().RestoreAsync());
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "documents", "file.txt")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
