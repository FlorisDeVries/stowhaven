using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using FlorisDeV.BackupApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Index tags are applied after a blob has already been moved. On an account with hierarchical
/// namespace and no blob-index support every tag write fails with FeatureNotEnabled; that used to
/// surface as a fatal "rename failed" for a rename that had succeeded.
/// </summary>
public class BlobStorageServiceIndexTagTests
{
    private static readonly Dictionary<string, string> RetiredTags = new() { ["state"] = "retired" };

    private static Mock<BlobClient> BlobWhoseTagWrite(Exception? failure)
    {
        var blob = new Mock<BlobClient>();
        var setup = blob.Setup(x => x.SetTagsAsync(
            It.IsAny<IDictionary<string, string>>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()));
        if (failure is null)
        {
            setup.ReturnsAsync(Mock.Of<Response>());
        }
        else
        {
            setup.ThrowsAsync(failure);
        }

        return blob;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AccountWithoutBlobIndexSupport_ReportsUnsupportedWithoutFailingTheMove()
    {
        var blob = BlobWhoseTagWrite(new RequestFailedException(400,
            "A required feature for this operation is not enabled on the account.", "FeatureNotEnabled", null));

        var supported = await BlobStorageService.TrySetIndexTagsAsync(blob.Object, RetiredTags, NullLogger.Instance);

        supported.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TransientTagFailure_KeepsTagsEnabledWithoutFailingTheMove()
    {
        var blob = BlobWhoseTagWrite(new RequestFailedException(503, "Server busy", "ServerBusy", null));

        var supported = await BlobStorageService.TrySetIndexTagsAsync(blob.Object, RetiredTags, NullLogger.Instance);

        supported.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SuccessfulTagWrite_KeepsTagsEnabled()
    {
        var blob = BlobWhoseTagWrite(null);

        var supported = await BlobStorageService.TrySetIndexTagsAsync(blob.Object, RetiredTags, NullLogger.Instance);

        supported.Should().BeTrue();
        blob.Verify(x => x.SetTagsAsync(RetiredTags, It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Cancellation_StillPropagates()
    {
        var blob = BlobWhoseTagWrite(new OperationCanceledException());

        var act = () => BlobStorageService.TrySetIndexTagsAsync(blob.Object, RetiredTags, NullLogger.Instance);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
