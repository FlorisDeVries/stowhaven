namespace FlorisDeV.BackupContracts.Manifest;

/// <summary>
/// Persisted metadata document for a run manifest. Large manifests are split so no single
/// document exceeds the state store's per-document size limit (Cosmos ~2MB): for
/// <see cref="SchemaVersion"/> &gt;= 2 the file/deletion entries live in separate
/// <see cref="RunManifestChunk"/> documents (see <see cref="ChunkCount"/>), and
/// <see cref="Files"/>/<see cref="Deleted"/> here are null.
///
/// <see cref="Files"/>/<see cref="Deleted"/> are populated only for legacy (v1) manifests that
/// were written inline as a single document before chunking was introduced; readers fall back to
/// them when <see cref="SchemaVersion"/> is less than 2.
/// </summary>
public sealed record RunManifestHeader
{
    public const int ChunkedSchemaVersion = 2;

    public int SchemaVersion { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public int DeletedCount { get; init; }

    /// <summary>Total number of <see cref="RunManifestChunk"/> documents for this run (v2+ only).</summary>
    public int ChunkCount { get; init; }

    /// <summary>Maximum number of entries stored in each chunk document (v2+ only).</summary>
    public int ChunkSize { get; init; }

    /// <summary>Legacy inline file entries; present only for v1 manifests.</summary>
    public List<ManifestFileEntry>? Files { get; init; }

    /// <summary>Legacy inline deletion paths; present only for v1 manifests.</summary>
    public List<string>? Deleted { get; init; }
}

/// <summary>
/// A slice of a run manifest's entries. Each chunk carries either a page of <see cref="Files"/>
/// or a page of <see cref="Deleted"/> paths (never both); the owning
/// <see cref="RunManifestHeader.ChunkCount"/> and per-chunk <see cref="Index"/> define reassembly
/// order. Kept well under the state store's per-document size limit.
/// </summary>
public sealed record RunManifestChunk
{
    public required string DeviceId { get; init; }
    public required string RunId { get; init; }

    /// <summary>Zero-based position of this chunk within the run's chunk sequence.</summary>
    public required int Index { get; init; }

    public List<ManifestFileEntry> Files { get; init; } = [];
    public List<string> Deleted { get; init; } = [];
}
