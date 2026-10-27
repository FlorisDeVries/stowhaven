using System.Net;
using System.Text.Json;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupContracts.State;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public sealed partial class ManifestManagerConcurrencyTests
{
    [Fact]
    public async Task SqliteTryCreate_DuplicatePreservesUpdatedDataETagAndSortFields()
    {
        var original = new CommitJob { Status = CommitJobStatus.Queued };
        var createdETag = await _store.TryCreateAsync("job", "partition", "id", original, sortKey: "original", sortValue: 1);
        Assert.NotNull(createdETag);
        original.Status = CommitJobStatus.Processing;
        original.FilesProcessed = 42;
        var updatedETag = await _store.UpsertAsync("job", "partition", "id", original,
            createdETag, sortKey: "current", sortValue: 100);

        Assert.Null(await _store.TryCreateAsync("job", "partition", "id", new CommitJob(), sortKey: "stale", sortValue: 0));

        var stored = await _store.GetAsync<CommitJob>("job", "partition", "id");
        Assert.Equal(updatedETag, stored!.ETag);
        Assert.Equal(CommitJobStatus.Processing, stored.Data.Status);
        Assert.Equal(42, stored.Data.FilesProcessed);
        var page = await _store.QueryAsync<CommitJob>(new DocumentQuery
            { Type = "job", PartitionKey = "partition", SortValueFrom = 100 });
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task SqliteTryCreate_ConcurrentInsertsHaveExactlyOneWinner()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await _store.TryCreateAsync("job", "partition", "id", new CommitJob { FilesProcessed = index });
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);
        var winningETag = Assert.Single(results, etag => etag != null);
        Assert.Equal(winningETag, (await _store.GetAsync<CommitJob>("job", "partition", "id"))!.ETag);
    }

    [Fact]
    public async Task SqliteTryCreate_IdentityIncludesDocumentTypeAndPartition()
    {
        Assert.NotNull(await _store.TryCreateAsync("job", "first", "id", new CommitJob()));
        Assert.NotNull(await _store.TryCreateAsync("job", "second", "id", new CommitJob()));
        Assert.NotNull(await _store.TryCreateAsync("other", "first", "id", new CommitJob()));
    }
}

public class CosmosStateDocumentCreationTests
{
    [Fact]
    public async Task TryCreate_SerializesEnvelopeAndReturnsCreatedETag()
    {
        var container = new Mock<Container>();
        var response = new ResponseMessage(HttpStatusCode.Created);
        response.Headers.Add("etag", "created-etag");
        JsonElement captured = default;
        using var cts = new CancellationTokenSource();
        container.Setup(x => x.CreateItemStreamAsync(It.IsAny<Stream>(), new PartitionKey("partition"),
                It.IsAny<ItemRequestOptions>(), cts.Token))
            .Returns(async (Stream stream, PartitionKey _, ItemRequestOptions _, CancellationToken token) =>
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                captured = document.RootElement.Clone();
                return response;
            });
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions);
        var etag = await store.TryCreateAsync("job", "partition", "id",
            new CommitJob { Status = CommitJobStatus.Processing }, "sort-key", 123, cts.Token);

        Assert.Equal("created-etag", etag);
        Assert.Equal("job:id", captured.GetProperty("id").GetString());
        Assert.Equal("partition", captured.GetProperty("partitionKey").GetString());
        Assert.Equal("job", captured.GetProperty("type").GetString());
        Assert.Equal("sort-key", captured.GetProperty("sortKey").GetString());
        Assert.Equal(123, captured.GetProperty("sortValue").GetInt64());
        Assert.Equal("processing", captured.GetProperty("data").GetProperty("status").GetString());
        container.Verify(x => x.CreateItemStreamAsync(It.IsAny<Stream>(), new PartitionKey("partition"),
            It.IsAny<ItemRequestOptions>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task TryCreate_ConflictReturnsNull()
    {
        var container = CreateContainer(HttpStatusCode.Conflict);
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions);
        Assert.Null(await store.TryCreateAsync("job", "partition", "id", new CommitJob()));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TryCreate_OtherFailuresPropagate(HttpStatusCode status)
    {
        var container = CreateContainer(status);
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions);
        var error = await Assert.ThrowsAsync<CosmosException>(() => store.TryCreateAsync("job", "partition", "id", new CommitJob()));
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task TryCreate_CancellationPropagates()
    {
        var container = new Mock<Container>();
        container.Setup(x => x.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryCreateAsync("job", "partition", "id", new CommitJob()));
    }

    private static Mock<Container> CreateContainer(HttpStatusCode status)
    {
        var container = new Mock<Container>();
        container.Setup(x => x.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ResponseMessage(status));
        return container;
    }
}
