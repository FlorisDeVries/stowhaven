using FlorisDeV.BackupApi.Data;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public sealed class CosmosInvestigationQueryTests
{
    [Fact]
    public async Task OwnersUseOrderedDistinctWithTenantParametersAndNativeContinuation()
    {
        var container = new Mock<Container>();
        var iterator = new Mock<FeedIterator<string>>();
        var empty = Response([], "empty-page-token");
        var page = Response(["owner-a", "owner-b"], "next-page-token");
        iterator.SetupGet(x => x.HasMoreResults).Returns(true);
        iterator.SetupSequence(x => x.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(empty).ReturnsAsync(page);
        QueryDefinition? query = null;
        QueryRequestOptions? requestOptions = null;
        string? suppliedToken = null;
        container.Setup(x => x.GetItemQueryIterator<string>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Callback<QueryDefinition, string, QueryRequestOptions>((definition, token, options) =>
            { query = definition; requestOptions = options; suppliedToken = token; })
            .Returns(iterator.Object);
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions);
        var result = await store.QueryDistinctValuesAsync(new DocumentQuery
        {
            Type = "device", FieldEquals = [new("tenantId", "tenant-a")], PageSize = 25, ContinuationToken = "previous-token"
        }, "userId");

        Assert.StartsWith("SELECT DISTINCT VALUE c.data.userId", query!.QueryText);
        Assert.EndsWith("ORDER BY c.data.userId ASC", query.QueryText);
        Assert.Contains("c.data.tenantId = @f0", query.QueryText);
        Assert.DoesNotContain("tenant-a", query.QueryText);
        Assert.Equal(25, requestOptions!.MaxItemCount);
        Assert.Equal("previous-token", suppliedToken);
        Assert.Equal(new[] { "owner-a", "owner-b" }, result.Values);
        Assert.Equal("next-page-token", result.NextContinuationToken);
        iterator.Verify(x => x.ReadNextAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task UnsafeDistinctFieldsNeverReachCosmos()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var store = new CosmosStateDocumentStore(container.Object, StateDocumentStoreExtensions.SerializerOptions);
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryDistinctValuesAsync(new DocumentQuery { Type = "device" }, "userId FROM c"));
        container.VerifyNoOtherCalls();
    }

    private static FeedResponse<string> Response(string[] values, string token)
    {
        var response = new Mock<FeedResponse<string>>();
        response.Setup(x => x.GetEnumerator()).Returns(() => ((IEnumerable<string>)values).GetEnumerator());
        response.SetupGet(x => x.ContinuationToken).Returns(token);
        return response.Object;
    }
}
