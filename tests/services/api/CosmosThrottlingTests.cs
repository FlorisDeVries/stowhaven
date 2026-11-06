using System.Net;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupContracts.State;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public sealed class CosmosThrottlingTests
{
    // Production failure: RUBudgetExceeded (substatus 3200) with retry-after hints of a few
    // milliseconds, so the SDK's own retries were exhausted within ~150 ms and the 429 escaped.
    private static CosmosException Throttled() =>
        new("Request rate is large.", HttpStatusCode.TooManyRequests, 3200, Guid.NewGuid().ToString(), 0.38);

    [Fact]
    public async Task Upsert_TransientThrottlingIsRetriedWithTheSameETag()
    {
        var container = new Mock<Container>();
        var response = new Mock<ItemResponse<CosmosStateDocumentStore.CosmosStateDocument>>();
        response.SetupGet(x => x.ETag).Returns("updated-etag");
        container.SetupSequence(x => x.UpsertItemAsync(It.IsAny<CosmosStateDocumentStore.CosmosStateDocument>(),
                It.IsAny<PartitionKey?>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Throttled())
            .ThrowsAsync(Throttled())
            .ReturnsAsync(response.Object);
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions);

        var etag = await store.UpsertAsync("fileversion", "partition", "id", new CommitJob(), etag: "current-etag");

        Assert.Equal("updated-etag", etag);
        var calls = container.Invocations.Where(i => i.Method.Name == nameof(Container.UpsertItemAsync)).ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Equal("current-etag", ((ItemRequestOptions)call.Arguments[2]).IfMatchEtag));
    
    }

    private static readonly CosmosThrottleRetryPolicy FastRetry = new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(2)
    };

    [Fact]
    public async Task Upsert_ThrottlingBeyondTheBudgetPropagates()
    {
        var container = new Mock<Container>();
        container.Setup(x => x.UpsertItemAsync(It.IsAny<CosmosStateDocumentStore.CosmosStateDocument>(),
                It.IsAny<PartitionKey?>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Throttled());
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions,
            FastRetry with { Budget = TimeSpan.FromMilliseconds(10) });

        var error = await Assert.ThrowsAsync<CosmosException>(() =>
            store.UpsertAsync("fileversion", "partition", "id", new CommitJob()));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(3200, error.SubStatusCode);
    }

    [Fact]
    public async Task TryCreate_ThrottledResponseIsRetriedWithTheFullPayload()
    {
        var container = new Mock<Container>();
        var created = new ResponseMessage(HttpStatusCode.Created);
        created.Headers.Add("etag", "created-etag");
        var payloadLengths = new List<long>();
        var responses = new Queue<ResponseMessage>([new ResponseMessage(HttpStatusCode.TooManyRequests), created]);
        container.Setup(x => x.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, PartitionKey, ItemRequestOptions, CancellationToken>((stream, _, _, _) =>
                payloadLengths.Add(stream.Length - stream.Position))
            .ReturnsAsync(() => responses.Dequeue());
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions, FastRetry);

        Assert.Equal("created-etag", await store.TryCreateAsync("job", "partition", "id", new CommitJob()));
        Assert.Equal(2, payloadLengths.Count);
        Assert.True(payloadLengths[0] > 0);
        Assert.Equal(payloadLengths[0], payloadLengths[1]);
    }

    [Fact]
    public async Task Query_ThrottledPageResumesFromTheLastPageBoundary()
    {
        var container = new Mock<Container>();
        var first = new Mock<FeedIterator<string>>();
        first.SetupGet(x => x.HasMoreResults).Returns(true);
        first.SetupSequence(x => x.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page([], "after-empty-page"))
            .ThrowsAsync(Throttled());
        var resumed = new Mock<FeedIterator<string>>();
        resumed.SetupGet(x => x.HasMoreResults).Returns(true);
        resumed.Setup(x => x.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Page(["owner-a"], "next"));
        var tokens = new List<string?>();
        var iterators = new Queue<FeedIterator<string>>([first.Object, resumed.Object]);
        container.Setup(x => x.GetItemQueryIterator<string>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Callback<QueryDefinition, string, QueryRequestOptions>((_, token, _) => tokens.Add(token))
            .Returns(() => iterators.Dequeue());
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions, FastRetry);

        var result = await store.QueryDistinctValuesAsync(
            new DocumentQuery { Type = "device", ContinuationToken = "previous" }, "userId");

        Assert.Equal(new[] { "owner-a" }, result.Values);
        Assert.Equal("next", result.NextContinuationToken);
        Assert.Equal(new string?[] { "previous", "after-empty-page" }, tokens);
    }

    private static FeedResponse<string> Page(string[] values, string token)
    {
        var response = new Mock<FeedResponse<string>>();
        response.Setup(x => x.GetEnumerator()).Returns(() => ((IEnumerable<string>)values).GetEnumerator());
        response.SetupGet(x => x.ContinuationToken).Returns(token);
        return response.Object;
    }
}
