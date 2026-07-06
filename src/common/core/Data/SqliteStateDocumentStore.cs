using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlorisDeV.BackupApi.Data;

/// <summary>
/// SQLite implementation of <see cref="IStateDocumentStore"/> for local development.
/// A single shared database file (WAL mode) serves both the API and the worker, mirroring
/// the shared Cosmos database in production. Field filters use json_extract on the stored
/// payload; continuation tokens are base64-encoded offsets.
/// </summary>
public sealed class SqliteStateDocumentStore : IStateDocumentStore
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _serializerOptions;

    public SqliteStateDocumentStore(string filePath, JsonSerializerOptions serializerOptions)
    {
        _serializerOptions = serializerOptions;

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = filePath }.ToString();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS documents(
                type TEXT NOT NULL,
                partition TEXT NOT NULL,
                id TEXT NOT NULL,
                sort_key TEXT NULL,
                sort_value INTEGER NULL,
                etag TEXT NOT NULL,
                json TEXT NOT NULL,
                PRIMARY KEY(type, partition, id)
            );
            CREATE INDEX IF NOT EXISTS ix_documents_type_sort_value ON documents(type, sort_value);
            CREATE INDEX IF NOT EXISTS ix_documents_type_partition_sort_key ON documents(type, partition, sort_key);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<StateDocument<T>?> GetAsync<T>(string type, string partitionKey, string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json, etag FROM documents WHERE type=@type AND partition=@partition AND id=@id";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@partition", partitionKey);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var data = Deserialize<T>(type, id, reader.GetString(0));
        return new StateDocument<T>(data, reader.GetString(1));
    }

    public async Task<string> UpsertAsync<T>(string type, string partitionKey, string id, T data,
        string? etag = null, string? sortKey = null, long? sortValue = null,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(data, _serializerOptions);
        var newETag = Guid.NewGuid().ToString("N");

        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();

        if (string.IsNullOrEmpty(etag))
        {
            command.CommandText =
                """
                INSERT INTO documents(type, partition, id, sort_key, sort_value, etag, json)
                VALUES(@type, @partition, @id, @sortKey, @sortValue, @etag, @json)
                ON CONFLICT(type, partition, id)
                DO UPDATE SET sort_key=@sortKey, sort_value=@sortValue, etag=@etag, json=@json
                """;
        }
        else
        {
            command.CommandText =
                """
                UPDATE documents
                SET sort_key=@sortKey, sort_value=@sortValue, etag=@etag, json=@json
                WHERE type=@type AND partition=@partition AND id=@id AND etag=@expectedETag
                """;
            command.Parameters.AddWithValue("@expectedETag", etag);
        }

        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@partition", partitionKey);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@sortKey", (object?)sortKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@sortValue", (object?)sortValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@etag", newETag);
        command.Parameters.AddWithValue("@json", json);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new StateConcurrencyException(type, id);
        }

        return newETag;
    }

    public async Task DeleteAsync(string type, string partitionKey, string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM documents WHERE type=@type AND partition=@partition AND id=@id";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@partition", partitionKey);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StateDocumentPage<T>> QueryAsync<T>(DocumentQuery query,
        CancellationToken cancellationToken = default)
    {
        var offset = DecodeContinuationToken(query.ContinuationToken);

        var sql = new StringBuilder("SELECT json, etag FROM documents WHERE type=@type");
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@type", query.Type);

        if (!string.IsNullOrEmpty(query.PartitionKey))
        {
            sql.Append(" AND partition=@partition");
            command.Parameters.AddWithValue("@partition", query.PartitionKey);
        }

        for (var i = 0; i < query.FieldEquals.Count; i++)
        {
            var filter = query.FieldEquals[i];
            ValidateFieldName(filter.FieldName);
            sql.Append($" AND json_extract(json, '$.{filter.FieldName}') = @f{i}");
            command.Parameters.AddWithValue($"@f{i}", filter.Value);
        }

        if (query.SortValueFrom.HasValue)
        {
            sql.Append(" AND sort_value >= @sortFrom");
            command.Parameters.AddWithValue("@sortFrom", query.SortValueFrom.Value);
        }

        if (query.SortValueTo.HasValue)
        {
            sql.Append(" AND sort_value <= @sortTo");
            command.Parameters.AddWithValue("@sortTo", query.SortValueTo.Value);
        }

        sql.Append(query.Order switch
        {
            DocumentOrder.SortKeyAscending => " ORDER BY sort_key ASC",
            DocumentOrder.SortValueDescending => " ORDER BY sort_value DESC",
            _ => " ORDER BY rowid ASC"
        });

        // Fetch one extra row to determine whether a further page exists.
        sql.Append(" LIMIT @limit OFFSET @offset");
        command.Parameters.AddWithValue("@limit", query.PageSize + 1);
        command.Parameters.AddWithValue("@offset", offset);
        command.CommandText = sql.ToString();

        var items = new List<StateDocument<T>>(query.PageSize);
        var hasMore = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (items.Count == query.PageSize)
            {
                hasMore = true;
                break;
            }

            var data = Deserialize<T>(query.Type, "<query>", reader.GetString(0));
            items.Add(new StateDocument<T>(data, reader.GetString(1)));
        }

        return new StateDocumentPage<T>(
            items,
            hasMore ? EncodeContinuationToken(offset + items.Count) : null);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private T Deserialize<T>(string type, string id, string json)
        => JsonSerializer.Deserialize<T>(json, _serializerOptions)
           ?? throw new InvalidOperationException($"Stored document {type} '{id}' could not be deserialized.");

    private static void ValidateFieldName(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName) || !fieldName.All(static c => char.IsAsciiLetterOrDigit(c)))
        {
            throw new ArgumentException($"Invalid document filter field name '{fieldName}'.");
        }
    }

    private static string EncodeContinuationToken(int offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static int DecodeContinuationToken(string? continuationToken)
    {
        if (string.IsNullOrWhiteSpace(continuationToken))
        {
            return 0;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken));
            if (int.TryParse(text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset >= 0)
            {
                return offset;
            }
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("Invalid continuation token.", nameof(continuationToken));
    }
}
