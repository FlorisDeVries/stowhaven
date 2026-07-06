namespace FlorisDeV.BackupApi.Data;

/// <summary>
/// Persistence abstraction for backup state documents. Implementations exist for
/// Azure Cosmos DB (production) and SQLite (local development). Documents are
/// addressed by (type, partitionKey, id) and support optimistic concurrency via
/// ETags plus paged queries
/// </summary>
public interface IStateDocumentStore
{
    Task<StateDocument<T>?> GetAsync<T>(string type, string partitionKey, string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces a document and returns the new ETag. When <paramref name="etag"/>
    /// is provided the write is conditional and throws <see cref="StateConcurrencyException"/>
    /// if the stored document has changed.
    /// </summary>
    Task<string> UpsertAsync<T>(string type, string partitionKey, string id, T data,
        string? etag = null, string? sortKey = null, long? sortValue = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string type, string partitionKey, string id,
        CancellationToken cancellationToken = default);

    Task<StateDocumentPage<T>> QueryAsync<T>(DocumentQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record StateDocument<T>(T Data, string ETag);

public sealed record StateDocumentPage<T>(IReadOnlyList<StateDocument<T>> Items, string? NextContinuationToken);

/// <summary>
/// A paged document query. Field equality filters address top-level properties of
/// the document payload by their serialized (camelCase) name and compare string
/// representations; range filters apply to the numeric sort value assigned at write
/// time (e.g. epoch milliseconds).
/// </summary>
public sealed record DocumentQuery
{
    public required string Type { get; init; }
    public string? PartitionKey { get; init; }
    public IReadOnlyList<DocumentFieldEquals> FieldEquals { get; init; } = [];
    public long? SortValueFrom { get; init; }
    public long? SortValueTo { get; init; }
    public DocumentOrder Order { get; init; } = DocumentOrder.None;
    public int PageSize { get; init; } = 100;
    public string? ContinuationToken { get; init; }
}

public sealed record DocumentFieldEquals(string FieldName, string Value);

public enum DocumentOrder
{
    None,
    SortKeyAscending,
    SortValueDescending
}

/// <summary>Keyed-service names for the logical stores.</summary>
public static class StateStores
{
    public const string Manifest = "manifest";
    public const string DeviceRegistry = "device-registry";
}

public sealed class StateConcurrencyException(string type, string id)
    : Exception($"Concurrent update detected for {type} '{id}'.")
{
    public string DocumentType { get; } = type;
    public string DocumentId { get; } = id;
}
