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

        // Normalize the path
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

        // Check if directory exists
        if (!Directory.Exists(normalizedPath))
        {
            return new ValidationResult(
                ValidationSeverity.Error,
                $"Backup target directory does not exist: {normalizedPath}");
        }

        // Check if we have read access
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

        // Check if backing up root - warn but allow
        if (IsRootOrSystemDrive(normalizedPath))
        {
            return new ValidationResult(
                ValidationSeverity.Warning,
                $"Backing up entire system drive ({normalizedPath}) is not recommended. " +
                $"Consider using: {GetRecommendedUserDirectory()} or add comprehensive exclusions. " +
                "See .backupignore for exclusion patterns.");
        }

        // Check if backing up pure system directory (not just root)
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
    /// </summary>
    public static string GetRecommendedUserDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: C:\Users\{username}
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(userProfile) 
                ? Path.Combine(@"C:\Users", Environment.UserName)
                : userProfile;
        }
        else
        {
            // Linux/macOS: /home/{username} or /Users/{username}
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home)
                ? Path.Combine("/home", Environment.UserName)
                : home;
        }
    }

    /// <summary>
    /// Gets comprehensive recommended exclusions for system files, caches, and temp files.
    /// These should be added to .backupignore by default.
    /// </summary>
    public static string[] GetRecommendedExclusions()
    {
        var exclusions = new List<string>
        {
            // Cross-platform
            "**/.git/**",
            "**/.svn/**",
            "**/node_modules/**",
            "**/__pycache__/**",
            "**/venv/**",
            "**/virtualenv/**",
            "**/.venv/**",
            "**/target/**",         // Rust/Java build output
            "**/bin/**",            // Build outputs
            "**/obj/**",            // .NET build outputs
            "**/.cache/**",
            "**/.tmp/**",
            "**/*.tmp",
            "**/*.temp",
            "**/*.log",
            "**/Thumbs.db",
            "**/.DS_Store",
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            exclusions.AddRange(new[]
            {
                // Windows system
                "**/Windows/**",
                "**/Program Files/**",
                "**/Program Files (x86)/**",
                "**/ProgramData/**",
                "**/System Volume Information/**",
                "**/$Recycle.Bin/**",
                "**/pagefile.sys",
                "**/hiberfil.sys",
                "**/swapfile.sys",
                
                // Windows user caches
                "**/AppData/Local/Temp/**",
                "**/AppData/Local/Microsoft/Windows/Temporary Internet Files/**",
                "**/AppData/Local/Microsoft/Windows/INetCache/**",
                "**/AppData/Local/Packages/*/TempState/**",
                "**/AppData/Local/CrashDumps/**",
                
                // Browser caches
                "**/AppData/Local/Google/Chrome/User Data/*/Cache/**",
                "**/AppData/Local/Mozilla/Firefox/Profiles/*/cache2/**",
                "**/AppData/Local/Microsoft/Edge/User Data/*/Cache/**",
            });
        }
        else
        {
            exclusions.AddRange(new[]
            {
                // Unix system directories
                "/bin/**",
                "/sbin/**",
                "/usr/bin/**",
                "/usr/sbin/**",
                "/usr/lib/**",
                "/lib/**",
                "/lib64/**",
                "/boot/**",
                "/sys/**",
                "/proc/**",
                "/dev/**",
                "/run/**",
                "/var/cache/**",
                "/var/tmp/**",
                "/tmp/**",
                
                // Linux/macOS user caches
                "**/.cache/**",
                "**/.local/share/Trash/**",
                "**/Library/Caches/**",
                "**/Library/Logs/**",
                "**/.Trash/**",
                
                // Browser caches (Unix)
                "**/.mozilla/firefox/*/cache2/**",
                "**/.config/google-chrome/*/Cache/**",
                "**/Library/Application Support/Google/Chrome/*/Cache/**",
            });
        }

        return exclusions.ToArray();
    }

    /// <summary>
    /// Creates a default .backupignore file content with recommended exclusions.
    /// </summary>
    public static string GenerateDefaultBackupIgnoreContent()
    {
        var content = new System.Text.StringBuilder();
        content.AppendLine("# Backup Ignore File");
        content.AppendLine("# Patterns use glob syntax (similar to .gitignore)");
        content.AppendLine("# Lines starting with # are comments");
        content.AppendLine();
        content.AppendLine("# System files");
        content.AppendLine("**/.DS_Store");
        content.AppendLine("**/Thumbs.db");
        content.AppendLine("**/desktop.ini");
        content.AppendLine();
        content.AppendLine("# Temporary files");
        content.AppendLine("**/*.tmp");
        content.AppendLine("**/*.temp");
        content.AppendLine("**/.tmp");
        content.AppendLine();
        content.AppendLine("# Logs");
        content.AppendLine("**/*.log");
        content.AppendLine();
        content.AppendLine("# Version control");
        content.AppendLine("**/.git/**");
        content.AppendLine("**/.svn/**");
        content.AppendLine();
        content.AppendLine("# Development artifacts");
        content.AppendLine("**/node_modules/**");
        content.AppendLine("**/__pycache__/**");
        content.AppendLine("**/venv/**");
        content.AppendLine("**/bin/**");
        content.AppendLine("**/obj/**");
        content.AppendLine("**/target/**");
        content.AppendLine();
        content.AppendLine("# Caches");
        content.AppendLine("**/.cache/**");
        content.AppendLine("**/Cache/**");
        content.AppendLine();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            content.AppendLine("# Windows specific");
            content.AppendLine("**/AppData/Local/Temp/**");
            content.AppendLine("**/AppData/Local/Microsoft/Windows/INetCache/**");
            content.AppendLine("**/$Recycle.Bin/**");
        }
        else
        {
            content.AppendLine("# Unix specific");
            content.AppendLine("**/tmp/**");
            content.AppendLine("**/.Trash/**");
            content.AppendLine("**/Library/Caches/**");
        }

        return content.ToString();
    }

    private static bool IsRootOrSystemDrive(string path)
    {
        var normalizedPath = Path.GetFullPath(path);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Check if it's just a drive root like "C:\" 
            return normalizedPath.Length == 3 && 
                   char.IsLetter(normalizedPath[0]) && 
                   normalizedPath[1] == ':' && 
                   normalizedPath[2] == Path.DirectorySeparatorChar;
        }
        else
        {
            // Check if it's Unix root
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
