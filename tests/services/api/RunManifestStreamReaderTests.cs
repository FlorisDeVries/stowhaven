using System.Text;
using System.Text.Json;
using FluentAssertions;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Manifest;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Tests for the incremental run-manifest reader. The reader parses through a fixed-size buffer, so
/// the cases that matter are entries and tokens that straddle buffer boundaries.
/// </summary>
public class RunManifestStreamReaderTests
{
    private static byte[] Serialize(RunManifest manifest)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

    private static ManifestFileEntry Entry(int index, int padding = 0) => new()
    {
        TargetName = "documents",
        RelativePath = $"dir{index}/file{index}{new string('p', padding)}.txt",
        UniqueFileId = $"{new string('a', 64)}_20260726T120000Z_{index:x8}",
        Sha256 = new string('b', 64),
        Size = 1000 + index,
        Mtime = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero)
    };

    private static RunManifest Manifest(int fileCount, int deletedCount, int padding = 0) => new()
    {
        DeviceId = "d".PadRight(32, '0'),
        RunId = "r".PadRight(32, '0'),
        Files = Enumerable.Range(0, fileCount).Select(i => Entry(i, padding)).ToList(),
        Deleted = Enumerable.Range(0, deletedCount).Select(i => $"documents/gone{i}.txt").ToList()
    };

    private static async Task<(List<ManifestFileEntry> Files, List<string> Deleted)> ReadAsync(byte[] json)
    {
        await using var stream = new MemoryStream(json, writable: false);

        var files = new List<ManifestFileEntry>();
        var deleted = new List<string>();

        await foreach (var item in RunManifestStreamReader.ReadAsync(stream))
        {
            if (item.File is { } file)
            {
                files.Add(file);
            }
            else
            {
                deleted.Add(item.DeletedPath!);
            }
        }

        return (files, deleted);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadAsync_ReturnsEveryEntryInOrder()
    {
        // Arrange
        var manifest = Manifest(fileCount: 5, deletedCount: 3);

        // Act
        var (files, deleted) = await ReadAsync(Serialize(manifest));

        // Assert
        files.Should().HaveCount(5);
        files.Select(f => f.RelativePath).Should().ContainInOrder(manifest.Files.Select(f => f.RelativePath));
        files[0].UniqueFileId.Should().Be(manifest.Files[0].UniqueFileId);
        files[0].Sha256.Should().Be(manifest.Files[0].Sha256);
        files[0].Size.Should().Be(manifest.Files[0].Size);
        files[0].Mtime.Should().Be(manifest.Files[0].Mtime);
        files[0].TargetName.Should().Be("documents");
        deleted.Should().ContainInOrder(manifest.Deleted);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadAsync_WhenManifestSpansManyBuffers_ReturnsEveryEntry()
    {
        // Arrange: comfortably larger than the reader's internal buffer, so entries land across
        // boundaries and the parse has to resume mid-array repeatedly.
        var manifest = Manifest(fileCount: 5_000, deletedCount: 1_000);

        // Act
        var (files, deleted) = await ReadAsync(Serialize(manifest));

        // Assert
        files.Should().HaveCount(5_000);
        deleted.Should().HaveCount(1_000);
        files[^1].UniqueFileId.Should().Be(manifest.Files[^1].UniqueFileId);
        deleted[^1].Should().Be(manifest.Deleted[^1]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadAsync_WhenSingleEntryExceedsBuffer_StillReturnsIt()
    {
        // Arrange: one entry padded past the 64KB pull size, forcing the pipe to grow its buffer
        // rather than stall on a permanently incomplete value.
        var manifest = Manifest(fileCount: 3, deletedCount: 1, padding: 90_000);

        // Act
        var (files, deleted) = await ReadAsync(Serialize(manifest));

        // Assert
        files.Should().HaveCount(3);
        files.Should().AllSatisfy(f => f.RelativePath.Should().Contain(new string('p', 90_000)));
        deleted.Should().HaveCount(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadAsync_WhenArraysAreEmpty_ReturnsNothing()
    {
        // Act
        var (files, deleted) = await ReadAsync(Serialize(Manifest(fileCount: 0, deletedCount: 0)));

        // Assert
        files.Should().BeEmpty();
        deleted.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadAsync_WhenDeletedPrecedesFiles_ReadsBoth()
    {
        // Arrange: property order is not guaranteed by JSON, so the reader must not depend on it.
        const string json = """
        {
          "schemaVersion": 1,
          "deleted": ["documents/gone.txt"],
          "runId": "r",
          "files": [
            {
              "targetName": "documents",
              "relativePath": "a.txt",
              "uniqueFileId": "uid-a",
              "sha256": "hash-a",
              "size": 12,
              "mtime": "2026-07-26T12:00:00+00:00"
            }
          ]
        }
        """;

        // Act
        var (files, deleted) = await ReadAsync(Encoding.UTF8.GetBytes(json));

        // Assert
        files.Should().HaveCount(1);
        files[0].UniqueFileId.Should().Be("uid-a");
        deleted.Should().BeEquivalentTo(["documents/gone.txt"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadAsync_IgnoresUnknownPropertiesIncludingNestedArrays()
    {
        // Arrange: an unrelated array must be walked past without being mistaken for manifest content.
        const string json = """
        {
          "schemaVersion": 1,
          "extra": [{"files": [{"nope": 1}]}, 2, 3],
          "files": [
            {
              "relativePath": "a.txt",
              "uniqueFileId": "uid-a",
              "sha256": "hash-a",
              "size": 1,
              "mtime": "2026-07-26T12:00:00+00:00"
            }
          ],
          "deleted": []
        }
        """;

        // Act
        var (files, deleted) = await ReadAsync(Encoding.UTF8.GetBytes(json));

        // Assert
        files.Should().HaveCount(1);
        files[0].UniqueFileId.Should().Be("uid-a");
        deleted.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadAsync_WhenJsonIsTruncated_Throws()
    {
        // Arrange: a partially written manifest must fail loudly rather than silently yield a subset.
        var json = Serialize(Manifest(fileCount: 200, deletedCount: 0));
        var truncated = json[..(json.Length / 2)];

        // Act
        var act = async () => await ReadAsync(truncated);

        // Assert
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadAsync_WhenFilesIsNotAnArray_Throws()
    {
        // Arrange
        var json = Encoding.UTF8.GetBytes("""{"files": {"a": 1}, "deleted": []}""");

        // Act
        var act = async () => await ReadAsync(json);

        // Assert
        await act.Should().ThrowAsync<JsonException>();
    }
}
