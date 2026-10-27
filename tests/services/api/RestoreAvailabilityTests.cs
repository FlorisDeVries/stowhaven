using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Responses;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public class RestoreAvailabilityTests
{
    [Theory]
    [InlineData(false, false, false, RestoreFileAvailability.Ready, 0)]
    [InlineData(true, false, false, RestoreFileAvailability.Archived, 0)]
    [InlineData(true, false, true, RestoreFileAvailability.Rehydrating, 1)]
    [InlineData(true, true, true, RestoreFileAvailability.Rehydrating, 0)]
    public async Task Rehydration_IsRequestedOnlyWhenArchivedAndNotAlreadyPending(
        bool archived, bool pending, bool request, RestoreFileAvailability expected, int tierRequests)
    {
        var blob = new Mock<BlobClient>();
        blob.Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobProperties(accessTier: archived ? "Archive" : "Hot",
                archiveStatus: pending ? "rehydrate-pending-to-hot" : null), Mock.Of<Response>()));
        blob.Setup(x => x.SetAccessTierAsync(AccessTier.Hot, It.IsAny<BlobRequestConditions>(), RehydratePriority.Standard, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());
        var container = new Mock<BlobContainerClient>();
        container.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        var storage = new Mock<IBlobStorageService>();
        storage.Setup(x => x.GetContainerClientAsync(It.IsAny<CancellationToken>())).ReturnsAsync(container.Object);
        var service = new RestoreAvailabilityService(storage.Object);
        Assert.Equal(expected, await service.EnsureAvailableAsync(Guid.NewGuid(), "file-id", request, CancellationToken.None));
        blob.Verify(x => x.SetAccessTierAsync(AccessTier.Hot, It.IsAny<BlobRequestConditions>(), RehydratePriority.Standard,
            It.IsAny<CancellationToken>()), Times.Exactly(tierRequests));
    }
}
