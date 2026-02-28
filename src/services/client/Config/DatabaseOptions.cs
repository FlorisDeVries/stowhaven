namespace FlorisDeV.BackupClient.Config;

/// <summary>
/// Configuration options for the local SQLite database.
/// </summary>
public class DatabaseOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Database";

    /// <summary>
    /// Full path to the SQLite database file.
    /// If not specified, defaults to user's LocalApplicationData folder.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the database path, using default location if not configured.
    /// </summary>
    public string GetDatabasePath()
    {
        if (!string.IsNullOrWhiteSpace(FilePath))
            return FilePath;

        // Default: %LOCALAPPDATA%/FlorisDeV/BackupClient/backup-state.db (Windows)
        // or ~/.local/share/FlorisDeV/BackupClient/backup-state.db (Linux/Mac)
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataPath, "FlorisDeV", "BackupClient", "backup-state.db");
    }
}
