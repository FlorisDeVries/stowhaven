namespace FlorisDeV.BackupWorker.Services;

/// <summary>
/// Thrown when a staged blob fails validation against its manifest entry (size, SHA-256, or the
/// blob being absent). This is a non-fatal error for the backup run; the file is skipped and will be re-detected and retried on the next backup.
/// </summary>
public sealed class StagedBlobValidationException : Exception
{
    public StagedBlobValidationException(string message) : base(message)
    {
    }

    public StagedBlobValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
