namespace FlorisDeV.BackupClient.Services;

internal static class PathIdentity
{
    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string SqliteCollation => OperatingSystem.IsWindows() ? "NOCASE" : "BINARY";
}
