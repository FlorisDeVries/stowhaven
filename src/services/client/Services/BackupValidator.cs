using System.Runtime.InteropServices;

namespace FlorisDeV.BackupClient.Services;

/// <summary>
/// Validation result with severity level.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>No issues detected.</summary>
    None,
    /// <summary>Warning - backup will work but may be suboptimal.</summary>
    Warning,
    /// <summary>Error - backup cannot proceed.</summary>
    Error
}

/// <summary>
/// Result of validating a backup directory.
/// </summary>
public record ValidationResult(
    ValidationSeverity Severity,
    string? Message = null);

/// <summary>
/// Validates backup configuration and provides smart defaults.
/// Uses warnings instead of hard blocks to allow user flexibility.
/// </summary>
public static class BackupValidator
{
    /// <summary>
    /// Validates the backup target directory.
    /// Returns validation result instead of throwing for better UX.
    /// </summary>
    public static ValidationResult ValidateBackupDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new ValidationResult(
                ValidationSeverity.Error,
                "Backup target directory cannot be empty");
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(directoryPath);
        }
        catch (Exception ex)
        {
            return new ValidationResult(
                ValidationSeverity.Error,
                $"Invalid directory path: {ex.Message}");
        }

        if (!Directory.Exists(normalizedPath))
        {
            return new ValidationResult(
                ValidationSeverity.Error,
                $"Backup target directory does not exist: {normalizedPath}");
        }

        try
        {
            Directory.GetFiles(normalizedPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch (UnauthorizedAccessException)
        {
            return new ValidationResult(
                ValidationSeverity.Error,
                $"Insufficient permissions to read backup target directory: {normalizedPath}");
        }

        if (IsRootOrSystemDrive(normalizedPath))
        {
            return new ValidationResult(
                ValidationSeverity.Warning,
                $"Backing up entire system drive ({normalizedPath}) is not recommended. " +
                $"Consider using: {GetRecommendedUserDirectory()} or add comprehensive exclusions. " +
                "See .backupignore for exclusion patterns.");
        }

        if (IsPureSystemDirectory(normalizedPath))
        {
            return new ValidationResult(
                ValidationSeverity.Warning,
                $"Backing up system directory ({normalizedPath}) may include unnecessary OS files. " +
                "Ensure you have proper exclusions in .backupignore.");
        }

        return new ValidationResult(ValidationSeverity.None);
    }

    /// <summary>
    /// Gets the recommended default backup directory for the current user.
    /// Used in warning messages to suggest better alternatives.
    /// </summary>
    private static string GetRecommendedUserDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(userProfile) 
                ? Path.Combine(@"C:\Users", Environment.UserName)
                : userProfile;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home)
                ? Path.Combine("/home", Environment.UserName)
                : home;
        }
    }

    private static bool IsRootOrSystemDrive(string path)
    {
        var normalizedPath = Path.GetFullPath(path);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return normalizedPath.Length == 3 && 
                   char.IsLetter(normalizedPath[0]) && 
                   normalizedPath[1] == ':' && 
                   normalizedPath[2] == Path.DirectorySeparatorChar;
        }
        else
        {
            return normalizedPath == "/";
        }
    }

    private static bool IsPureSystemDirectory(string path)
    {
        var normalizedPath = Path.GetFullPath(path);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var systemPaths = new[] { @"\Windows", @"\Program Files", @"\Program Files (x86)", @"\ProgramData" };
            return systemPaths.Any(sp => 
                normalizedPath.EndsWith(sp, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.Contains(sp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var systemPaths = new[] { "/bin", "/sbin", "/usr", "/etc", "/sys", "/proc", "/dev", "/boot", "/var" };
            return systemPaths.Contains(normalizedPath);
        }
    }
}
