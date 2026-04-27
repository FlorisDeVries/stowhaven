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
    public required Dictionary<string, string> BackupTargets { get; init; }

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
    public string? IgnoreFilePath { get; init; }

    /// <summary>
    /// Maximum number of files to upload in parallel. Default is 4.
    /// Higher values may improve throughput but consume more resources.
    /// </summary>
    public int MaxParallelUploads { get; init; } = 4;

    /// <summary>
    /// Size threshold in bytes above which file upload progress will be tracked.
    /// Default is 10 MB. Set to 0 to track all files.
    /// </summary>
    public long LargeFileThresholdBytes { get; init; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Maximum number of retry attempts for transient failures (network errors, timeouts, throttling).
    /// Default is 3. Set to 0 to disable retries.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Initial delay for retry backoff in milliseconds. Default is 1000ms (1 second).
    /// Each retry doubles the delay (exponential backoff) up to MaxRetryDelayMs.
    /// </summary>
    public int RetryDelayMs { get; init; } = 1000;

    /// <summary>
    /// Maximum delay between retry attempts in milliseconds. Default is 30000ms (30 seconds).
    /// </summary>
    public int MaxRetryDelayMs { get; init; } = 30000;

    /// <summary>
    /// HTTP request timeout in seconds for API calls. Default is 300 (5 minutes).
    /// </summary>
    public int HttpTimeoutSeconds { get; } = 300;

    /// <summary>
    /// Blob upload timeout per attempt in seconds. Default is 600 (10 minutes).
    /// Large files may need longer timeouts.
    /// </summary>
    public int BlobUploadTimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Maximum percentage of files allowed to fail before considering the backup unsuccessful.
    /// Default is 5 (5%). Set to 100 to always succeed regardless of failures.
    /// </summary>
    public int MaxFailurePercentage { get; init; } = 5;

    /// <summary>
    /// Interval in seconds between commit status polling attempts.
    /// </summary>
    public int CommitStatusPollIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// Maximum time in seconds to wait for the server-side commit worker to complete.
    /// </summary>
    public int CommitStatusTimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Optional client-side encryption configuration. ServerSideOnly mode uploads plaintext and relies on Azure Storage encryption at rest.
    /// ClientAndServer mode encrypts files locally and writes a generated recovery phrase to disk for the user to store offline.
    /// </summary>
    public BackupEncryptionOptions Encryption { get; init; } = new();

    /// <summary>
    /// Restore command configuration used when running the client in restore mode.
    /// </summary>
    public BackupRestoreOptions Restore { get; init; } = new();
}

public sealed class BackupRestoreOptions
{
    public Guid? DeviceId { get; init; }
    public string? DestinationPath { get; init; }
    public string[] LogicalPaths { get; init; } = [];
    public int ListPageSize { get; init; } = 500;
    public bool OverwriteExisting { get; init; }
}

public sealed class BackupEncryptionOptions
{
    public BackupEncryptionMode Mode { get; init; } = BackupEncryptionMode.ServerSideOnly;

    /// <summary>
    /// Path to the local JSON file containing the generated zero-knowledge recovery phrase.
    /// If omitted, a file is created under the user's application data folder.
    /// </summary>
    public string? RecoveryPhraseFilePath { get; init; }

    /// <summary>
    /// PBKDF2 iteration count used to derive the master key from the recovery phrase.
    /// </summary>
    public int KdfIterations { get; init; } = 600_000;
}

public enum BackupEncryptionMode
{
    ServerSideOnly,
    ClientAndServer
}
