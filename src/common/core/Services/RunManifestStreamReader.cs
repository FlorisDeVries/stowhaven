using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FlorisDeV.BackupContracts.Manifest;

namespace FlorisDeV.BackupApi.Services;

/// <summary>
/// One entry from a run manifest: either a file entry or a deleted logical path, never both.
/// </summary>
public readonly record struct RunManifestStreamItem(ManifestFileEntry? File, string? DeletedPath);

/// <summary>
/// Reads <c>run-manifest.json</c> incrementally.
///
/// A run covering hundreds of thousands of files produces a manifest of a hundred megabytes or more.
/// Buffering that and deserializing it whole costs several times its size in managed memory (the raw
/// bytes, a UTF-16 string, then one object per entry), which does not fit the container's memory
/// limit. This reader pulls the document through a fixed-size buffer and yields one entry at a time,
/// so peak memory is set by the buffer and the largest single entry rather than by the entry count.
/// </summary>
public static class RunManifestStreamReader
{
    private static readonly byte[] FilesProperty = Encoding.UTF8.GetBytes("files");
    private static readonly byte[] DeletedProperty = Encoding.UTF8.GetBytes("deleted");

    private static readonly JsonSerializerOptions EntryOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Bytes fetched per pull. Grown automatically if a single entry does not fit.</summary>
    private const int BufferSize = 64 * 1024;

    private enum Phase
    {
        /// <summary>Walking tokens one at a time, looking for a known array property.</summary>
        Scanning,
        InFiles,
        InDeleted
    }

    /// <summary>
    /// Streams the manifest's file entries and deletions in document order. The caller keeps
    /// ownership of <paramref name="stream"/>.
    /// </summary>
    public static async IAsyncEnumerable<RunManifestStreamItem> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var pipe = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: BufferSize, leaveOpen: true));
        var state = new JsonReaderState();
        var phase = Phase.Scanning;

        // Entries decoded from the current buffer. Bounded by how many fit in one pull, because the
        // JSON reader is a ref struct and cannot stay alive across a yield.
        var decoded = new List<RunManifestStreamItem>();

        while (true)
        {
            var result = await pipe.ReadAsync(cancellationToken);
            var buffer = result.Buffer;

            decoded.Clear();
            var consumed = Decode(buffer, result.IsCompleted, ref state, ref phase, decoded);

            foreach (var item in decoded)
            {
                yield return item;
            }

            // Marking everything as examined lets the pipe grow its buffer when a single entry is
            // larger than what one pull returned.
            pipe.AdvanceTo(consumed, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        await pipe.CompleteAsync();
    }

    /// <summary>
    /// Decodes as many entries as the buffer holds, and returns how far the buffer was consumed. A
    /// partially buffered entry is left unconsumed so the next pull can retry it intact.
    /// </summary>
    private static SequencePosition Decode(
        in ReadOnlySequence<byte> buffer,
        bool isFinalBlock,
        ref JsonReaderState state,
        ref Phase phase,
        List<RunManifestStreamItem> decoded)
    {
        var reader = new Utf8JsonReader(buffer, isFinalBlock, state);

        while (true)
        {
            // Every branch rewinds to this checkpoint when it needs bytes it does not have yet.
            // Utf8JsonReader is a struct, so a copy restores both position and reader state.
            var checkpoint = reader;

            if (phase == Phase.Scanning)
            {
                if (!reader.Read())
                {
                    break;
                }

                // Token-by-token rather than TrySkip: skipping a value requires it to be fully
                // buffered, which for the 'files' array would mean holding the whole manifest.
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                var isFiles = reader.ValueTextEquals(FilesProperty);
                var isDeleted = !isFiles && reader.ValueTextEquals(DeletedProperty);

                if (!isFiles && !isDeleted)
                {
                    continue;
                }

                if (!reader.Read())
                {
                    reader = checkpoint;
                    break;
                }

                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw new JsonException(
                        $"Run manifest property '{(isFiles ? "files" : "deleted")}' is not an array.");
                }

                phase = isFiles ? Phase.InFiles : Phase.InDeleted;
                continue;
            }

            if (!reader.Read())
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                phase = Phase.Scanning;
                continue;
            }

            // Deserializing throws on a truncated value, so confirm the whole entry is buffered
            // first using a throwaway copy of the reader.
            var probe = reader;
            if (!probe.TrySkip())
            {
                reader = checkpoint;
                break;
            }

            if (phase == Phase.InFiles)
            {
                var entry = JsonSerializer.Deserialize<ManifestFileEntry>(ref reader, EntryOptions)
                    ?? throw new JsonException("Run manifest contains a null entry in 'files'.");
                decoded.Add(new RunManifestStreamItem(entry, null));
            }
            else
            {
                var path = JsonSerializer.Deserialize<string>(ref reader, EntryOptions)
                    ?? throw new JsonException("Run manifest contains a null path in 'deleted'.");
                decoded.Add(new RunManifestStreamItem(null, path));
            }
        }

        state = reader.CurrentState;
        return reader.Position;
    }
}
