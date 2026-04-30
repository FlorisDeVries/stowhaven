namespace FlorisDeV.BackupApi.Options;

public sealed class StaleStagingCleanupOptions
{
    public const string SectionName = "Operations:StaleStagingCleanup";

    public int OlderThanHours { get; init; } = 24;
    public int MaxDeletes { get; init; } = 500;
    public bool DryRun { get; init; }
}
