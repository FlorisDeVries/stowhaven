namespace FlorisDeV.BackupApi.Exceptions;

public class SecretStoreUnavailableException(
    string store,
    Exception inner
) : Exception($"Secret store '{store}' is unavailable.", inner)
{
    public string SecretStore { get; } = store;
}