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
    /// Named backup targets. Each target has a name (used as prefix in storage) and a directory path.
    /// Examples:
    /// - "user-profile": "C:\\Users\\John"
    /// - "projects": "D:\\Projects"
    /// - "photos": "E:\\Photos"
    /// The target name becomes part of the stored path (e.g., "user-profile/Documents/file.txt").
    /// </summary>
    public required Dictionary<string, string> BackupTargets { get; set; }

    /// <summary>
    /// Gets the effective backup targets with validated target names.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetEffectiveTargets()
    {
        if (BackupTargets.Count == 0)
        {
            throw new InvalidOperationException(
                "No backup targets configured. At least one target must be specified in 'BackupTargets'.");
        }

        foreach (var (name, _) in BackupTargets)
        {
            if (name.Contains('/') || name.Contains('\\'))
            {
                throw new InvalidOperationException(
                    $"Backup target name '{name}' cannot contain slashes. Use alphanumeric and hyphens only.");
            }
        }

        return BackupTargets;
    }

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

    /// <summary>
    /// Maximum number of files to upload in parallel. Default is 4.
    /// Higher values may improve throughput but consume more resources.
    /// </summary>
    public int MaxParallelUploads { get; set; } = 4;

    /// <summary>
    /// Size threshold in bytes above which file upload progress will be tracked.
    /// Default is 10 MB. Set to 0 to track all files.
    /// </summary>
    public long LargeFileThresholdBytes { get; set; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Maximum number of retry attempts for transient failures (network errors, timeouts, throttling).
    /// Default is 3. Set to 0 to disable retries.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Initial delay for retry backoff in milliseconds. Default is 1000ms (1 second).
    /// Each retry doubles the delay (exponential backoff) up to MaxRetryDelayMs.
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Maximum delay between retry attempts in milliseconds. Default is 30000ms (30 seconds).
    /// </summary>
    public int MaxRetryDelayMs { get; set; } = 30000;

    /// <summary>
    /// HTTP request timeout in seconds for API calls. Default is 300 (5 minutes).
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Blob upload timeout per attempt in seconds. Default is 600 (10 minutes).
    /// Large files may need longer timeouts.
    /// </summary>
    public int BlobUploadTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Maximum percentage of files allowed to fail before considering the backup unsuccessful.
    /// Default is 5 (5%). Set to 100 to always succeed regardless of failures.
    /// </summary>
    public int MaxFailurePercentage { get; set; } = 5;
}
