namespace FlorisDeV.BackupClient.Models;

/// <summary>
/// Represents the type of backup operation being performed.
/// </summary>
public enum BackupType
{
    /// <summary>
    /// Full backup - first backup run for a device, all files are backed up.
    /// </summary>
    Full,

    /// <summary>
    /// Incremental backup - only changed files since the last successful backup are uploaded.
    /// </summary>
    Incremental
}
