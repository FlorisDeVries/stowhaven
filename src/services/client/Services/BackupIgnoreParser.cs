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
    public static string[]? ReadIgnoreFile(string ignoreFilePath)
    {
        if (!File.Exists(ignoreFilePath))
            return null;

        var patterns = new List<string>();

        foreach (var line in File.ReadAllLines(ignoreFilePath))
        {
            var trimmedLine = line.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                continue;

            patterns.Add(trimmedLine);
        }

        return patterns.Count > 0 ? patterns.ToArray() : null;
    }

    /// <summary>
    /// Combines patterns from .backupignore file with additional patterns from config.
    /// </summary>
    /// <param name="ignoreFilePath">Path to the .backupignore file (optional)</param>
    /// <param name="additionalPatterns">Additional patterns from configuration (optional)</param>
    /// <returns>Combined array of all exclusion patterns</returns>
    public static string[]? GetCombinedPatterns(string? ignoreFilePath, string[]? additionalPatterns)
    {
        var filePatterns = !string.IsNullOrWhiteSpace(ignoreFilePath) 
            ? ReadIgnoreFile(ignoreFilePath) 
            : null;

        if (filePatterns == null && additionalPatterns == null)
            return null;

        if (filePatterns == null)
            return additionalPatterns;

        if (additionalPatterns == null)
            return filePatterns;

        return filePatterns.Concat(additionalPatterns).Distinct().ToArray();
    }
}
