namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Represents an exception which is thrown when a required secret cannot be found
/// in the specified secret store.
/// </summary>
public class SecretNotFoundException(
    string store,
    string name
) : Exception($"Required secret '{name}' was not found in secret store '{store}'.")
{
    public string SecretName { get; } = name;
    public string SecretStore { get; } = store;
}