using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using FluentAssertions;
using FlorisDeV.BackupApi.Services;
using Moq;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Tests the ADLS Gen2 move path's request pattern. Every file in a device's backup renames into the
/// same parent directory, so creating that directory per move would emit one conditional PUT — a 409
/// once it exists — for every file in the run.
/// </summary>
public class BlobStorageServiceRenameTests
{
    private static RequestFailedException ParentMissing()
        => new(404, "The upload destination's parent path does not exist.", "RenameDestinationParentPathNotFound", null);

    private static Mock<DataLakeFileClient> FileClientThatRenamesSuccessfully()
    {
        var fileClient = new Mock<DataLakeFileClient>();
        fileClient
            .Setup(x => x.RenameAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataLakeRequestConditions>(),
                It.IsAny<DataLakeRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<DataLakeFileClient>>());
        return fileClient;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Rename_WhenParentDirectoryExists_DoesNotTouchTheDirectory()
    {
        // Arrange
        var fileSystem = new Mock<DataLakeFileSystemClient>(MockBehavior.Strict);
        var fileClient = FileClientThatRenamesSuccessfully();

        // Act
        await BlobStorageService.RenameCreatingParentIfMissingAsync(
            fileSystem.Object, fileClient.Object, "devices/abc/files/file-1", CancellationToken.None);

        // Assert: the steady-state path issues exactly one request, the rename itself. A strict mock
        // on the file system means any directory call at all would fail the test.
        fileClient.Verify(x => x.RenameAsync(
            It.Is<string>(d => d == "devices/abc/files/file-1"),
            It.IsAny<string>(), It.IsAny<DataLakeRequestConditions>(),
            It.IsAny<DataLakeRequestConditions>(), It.IsAny<CancellationToken>()), Times.Once);
        fileSystem.VerifyNoOtherCalls();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Rename_WhenParentDirectoryMissing_CreatesItAndRetriesOnce()
    {
        // Arrange: the first rename reports the parent missing, the retry succeeds.
        var directory = new Mock<DataLakeDirectoryClient>();
        directory
            .Setup(x => x.CreateIfNotExistsAsync(
                It.IsAny<DataLakePathCreateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<PathInfo>>());

        var fileSystem = new Mock<DataLakeFileSystemClient>();
        fileSystem.Setup(x => x.GetDirectoryClient("devices/abc/files")).Returns(directory.Object);

        var attempts = 0;
        var fileClient = new Mock<DataLakeFileClient>();
        fileClient
            .Setup(x => x.RenameAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataLakeRequestConditions>(),
                It.IsAny<DataLakeRequestConditions>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++attempts == 1
                ? throw ParentMissing()
                : Task.FromResult(Mock.Of<Response<DataLakeFileClient>>()));

        // Act
        await BlobStorageService.RenameCreatingParentIfMissingAsync(
            fileSystem.Object, fileClient.Object, "devices/abc/files/file-1", CancellationToken.None);

        // Assert
        attempts.Should().Be(2);
        fileSystem.Verify(x => x.GetDirectoryClient("devices/abc/files"), Times.Once);
        directory.Verify(x => x.CreateIfNotExistsAsync(
            It.IsAny<DataLakePathCreateOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Rename_WhenRenameFailsForAnotherReason_PropagatesWithoutCreatingDirectory()
    {
        // Arrange: an unrelated failure must not be mistaken for a missing parent, so the caller's
        // copy/delete fallback still gets a chance to handle it.
        var fileSystem = new Mock<DataLakeFileSystemClient>(MockBehavior.Strict);
        var fileClient = new Mock<DataLakeFileClient>();
        fileClient
            .Setup(x => x.RenameAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataLakeRequestConditions>(),
                It.IsAny<DataLakeRequestConditions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "Forbidden", "AuthorizationFailure", null));

        // Act
        var act = async () => await BlobStorageService.RenameCreatingParentIfMissingAsync(
            fileSystem.Object, fileClient.Object, "devices/abc/files/file-1", CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<RequestFailedException>()).Which.ErrorCode.Should().Be("AuthorizationFailure");
        fileSystem.VerifyNoOtherCalls();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Rename_WhenDestinationHasNoParentSegment_DoesNotAttemptDirectoryCreate()
    {
        // Arrange: a root-level destination has no parent directory to create.
        var directory = new Mock<DataLakeDirectoryClient>();
        var fileSystem = new Mock<DataLakeFileSystemClient>();
        fileSystem.Setup(x => x.GetDirectoryClient(It.IsAny<string>())).Returns(directory.Object);

        var attempts = 0;
        var fileClient = new Mock<DataLakeFileClient>();
        fileClient
            .Setup(x => x.RenameAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataLakeRequestConditions>(),
                It.IsAny<DataLakeRequestConditions>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++attempts == 1
                ? throw ParentMissing()
                : Task.FromResult(Mock.Of<Response<DataLakeFileClient>>()));

        // Act
        await BlobStorageService.RenameCreatingParentIfMissingAsync(
            fileSystem.Object, fileClient.Object, "file-at-root", CancellationToken.None);

        // Assert
        attempts.Should().Be(2);
        fileSystem.Verify(x => x.GetDirectoryClient(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("RenameDestinationParentPathNotFound", true)]
    [InlineData("renamedestinationparentpathnotfound", true)]
    [InlineData("PathNotFound", false)]
    [InlineData("BlobNotFound", false)]
    [InlineData(null, false)]
    public void IsDestinationParentMissing_MatchesOnlyTheParentMissingErrorCode(string? errorCode, bool expected)
    {
        // Arrange: matched on error code rather than status, so it holds whichever status is paired.
        var exception = new RequestFailedException(404, "message", errorCode, null);

        // Act
        var result = BlobStorageService.IsDestinationParentMissing(exception);

        // Assert
        result.Should().Be(expected);
    }
}
