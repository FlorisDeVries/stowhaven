namespace FlorisDeV.BackupClient.Config;

/// <summary>
/// Configuration options for the backup client.
/// </summary>
public class BackupClientOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "BackupClient";

    /// <summary>
    /// The directory to back up.
    /// </summary>
    public required string BackupTargetDirectory { get; set; }

    /// <summary>
    /// Path to .backupignore file (supports .gitignore-style patterns).
    /// If not specified, looks for .backupignore in the backup target directory.
    /// </summary>
    public string? IgnoreFilePath { get; set; }

    /// <summary>
    /// Additional glob patterns to exclude from backup (e.g., "*.tmp", "node_modules/**").
    /// These are combined with patterns from the .backupignore file.
    /// </summary>
    public string[]? ExcludePatterns { get; set; }
}
