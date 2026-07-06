using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace FlorisDeV.BackupApi.Data;

/// <summary>
/// Cosmos DB (SQL API) implementation of <see cref="IStateDocumentStore"/>.
/// Documents are stored in an envelope { id, partitionKey, type, sortKey, sortValue, data }
/// in a container partitioned on /partitionKey. Enumeration uses native Cosmos queries
/// with server-side continuation tokens.
/// </summary>
public sealed class CosmosStateDocumentStore(Container container, JsonSerializerOptions serializerOptions) : IStateDocumentStore
{
    public async Task<StateDocument<T>?> GetAsync<T>(string type, string partitionKey, string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await container.ReadItemAsync<CosmosStateDocument>(
                BuildDocumentId(type, id),
                new PartitionKey(partitionKey),
                cancellationToken: cancellationToken);

            var data = response.Resource.Data.Deserialize<T>(serializerOptions)
                       ?? throw new InvalidOperationException($"Stored document {type} '{id}' could not be deserialized.");

            return new StateDocument<T>(data, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string> UpsertAsync<T>(string type, string partitionKey, string id, T data,
        string? etag = null, string? sortKey = null, long? sortValue = null,
        CancellationToken cancellationToken = default)
    {
        var document = new CosmosStateDocument
        {
            Id = BuildDocumentId(type, id),
            PartitionKey = partitionKey,
            Type = type,
            SortKey = sortKey,
            SortValue = sortValue,
            Data = JsonSerializer.SerializeToElement(data, serializerOptions)
        };

        try
        {
            var response = await container.UpsertItemAsync(
                document,
                new PartitionKey(partitionKey),
                string.IsNullOrEmpty(etag) ? null : new ItemRequestOptions { IfMatchEtag = etag },
                cancellationToken);

            return response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new StateConcurrencyException(type, id);
        }
    }

    public async Task DeleteAsync(string type, string partitionKey, string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await container.DeleteItemAsync<CosmosStateDocument>(
                BuildDocumentId(type, id),
                new PartitionKey(partitionKey),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent delete.
        }
    }

    public async Task<StateDocumentPage<T>> QueryAsync<T>(DocumentQuery query,
        CancellationToken cancellationToken = default)
    {
        var queryDefinition = BuildQueryDefinition(query);
        var requestOptions = new QueryRequestOptions
        {
            MaxItemCount = query.PageSize
        };

        if (!string.IsNullOrEmpty(query.PartitionKey))
        {
            requestOptions.PartitionKey = new PartitionKey(query.PartitionKey);
        }

        using var iterator = container.GetItemQueryIterator<CosmosStateDocument>(
            queryDefinition,
            string.IsNullOrWhiteSpace(query.ContinuationToken) ? null : query.ContinuationToken,
            requestOptions);

        var items = new List<StateDocument<T>>(query.PageSize);
        string? nextContinuation = null;

        // Cosmos may return empty result pages that still carry a continuation token;
        // keep reading until the page holds data or the query is drained.
        while (iterator.HasMoreResults && items.Count == 0)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            foreach (var document in response)
            {
                var data = document.Data.Deserialize<T>(serializerOptions)
                           ?? throw new InvalidOperationException($"Stored document {document.Type} '{document.Id}' could not be deserialized.");
                items.Add(new StateDocument<T>(data, document.ETag ?? string.Empty));
            }

            nextContinuation = response.ContinuationToken;
        }

        return new StateDocumentPage<T>(items, nextContinuation);
    }

    private static QueryDefinition BuildQueryDefinition(DocumentQuery query)
    {
        var sql = new StringBuilder("SELECT * FROM c WHERE c.type = @type");
        var parameters = new List<(string Name, object Value)> { ("@type", query.Type) };

        for (var i = 0; i < query.FieldEquals.Count; i++)
        {
            var filter = query.FieldEquals[i];
            ValidateFieldName(filter.FieldName);
            sql.Append($" AND c.data.{filter.FieldName} = @f{i}");
            parameters.Add(($"@f{i}", filter.Value));
        }

        if (query.SortValueFrom.HasValue)
        {
            sql.Append(" AND c.sortValue >= @sortFrom");
            parameters.Add(("@sortFrom", query.SortValueFrom.Value));
        }

        if (query.SortValueTo.HasValue)
        {
            sql.Append(" AND c.sortValue <= @sortTo");
            parameters.Add(("@sortTo", query.SortValueTo.Value));
        }

        sql.Append(query.Order switch
        {
            DocumentOrder.SortKeyAscending => " ORDER BY c.sortKey ASC",
            DocumentOrder.SortValueDescending => " ORDER BY c.sortValue DESC",
            _ => string.Empty
        });

        var definition = new QueryDefinition(sql.ToString());
        foreach (var (name, value) in parameters)
        {
            definition = definition.WithParameter(name, value);
        }

        return definition;
    }

    private static void ValidateFieldName(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName) || !fieldName.All(static c => char.IsAsciiLetterOrDigit(c)))
        {
            throw new ArgumentException($"Invalid document filter field name '{fieldName}'.");
        }
    }

    // Ids are prefixed with the type so different document kinds sharing a natural key
    // (e.g. a run and its manifest) never collide within a partition.
    private static string BuildDocumentId(string type, string id) => $"{type}:{id}";

    private sealed class CosmosStateDocument
    {
        public required string Id { get; set; }
        public required string PartitionKey { get; set; }
        public required string Type { get; set; }
        public string? SortKey { get; set; }
        public long? SortValue { get; set; }
        public required JsonElement Data { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("_etag")]
        public string? ETag { get; set; }
    }
}
