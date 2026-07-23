namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Reads and parses .backupignore files (similar to .gitignore format).
/// Supports comments, empty lines, and glob patterns.
/// </summary>
public static class BackupIgnoreParser
{
    /// <summary>
    /// Reads a .backupignore file and returns the list of exclusion patterns.
    /// </summary>
    /// <param name="ignoreFilePath">Path to the .backupignore file</param>
    /// <returns>Array of glob patterns to exclude, or null if file doesn't exist</returns>
    internal static string[]? ReadIgnoreFile(string ignoreFilePath)
    {
        if (!File.Exists(ignoreFilePath))
            return null;

        var patterns = new List<string>();

        foreach (var line in File.ReadAllLines(ignoreFilePath))
        {
            var trimmedLine = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                continue;

            patterns.Add(trimmedLine);
        }

        return patterns.Count > 0 ? patterns.ToArray() : null;
    }

    /// <summary>
    /// Gets patterns from .backupignore file.
    /// </summary>
    /// <param name="ignoreFilePath">Path to the .backupignore file (optional). A relative path is
    /// resolved against the executable's directory rather than the process's current working
    /// directory, so scheduled tasks/services find it regardless of their configured "start in" folder.</param>
    /// <returns>Array of exclusion patterns from the file, or null if file doesn't exist or path is null</returns>
    public static string[]? GetCombinedPatterns(string? ignoreFilePath)
    {
        if (string.IsNullOrWhiteSpace(ignoreFilePath))
            return null;

        var resolvedPath = Path.IsPathRooted(ignoreFilePath)
            ? ignoreFilePath
            : Path.Combine(AppContext.BaseDirectory, ignoreFilePath);

        return ReadIgnoreFile(resolvedPath);
    }
}
