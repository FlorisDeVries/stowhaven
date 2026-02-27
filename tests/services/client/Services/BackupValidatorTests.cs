using FlorisDeV.BackupClient.Services;
using Xunit;
using FluentAssertions;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Unit tests for BackupValidator static utility class.
/// </summary>
public class BackupValidatorTests : IDisposable
{
    private readonly string _validDirectory;
    private readonly string _restrictedDirectory;

    public BackupValidatorTests()
    {
        // Create a valid test directory
        _validDirectory = Path.Combine(Path.GetTempPath(), $"validator-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_validDirectory);

        // Create a directory with restricted permissions (best effort - may not work on all systems)
        _restrictedDirectory = Path.Combine(Path.GetTempPath(), $"restricted-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_restrictedDirectory);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenNull_ShouldReturnError()
    {
        // Act
        var result = BackupValidator.ValidateBackupDirectory(null!);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Error);
        result.Message.Should().Contain("cannot be empty");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenEmpty_ShouldReturnError()
    {
        // Act
        var result = BackupValidator.ValidateBackupDirectory("");

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Error);
        result.Message.Should().Contain("cannot be empty");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenWhitespace_ShouldReturnError()
    {
        // Act
        var result = BackupValidator.ValidateBackupDirectory("   ");

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Error);
        result.Message.Should().Contain("cannot be empty");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenInvalidPath_ShouldReturnError()
    {
        // Arrange - Use invalid characters for path (platform-specific)
        string invalidPath;
        if (OperatingSystem.IsWindows())
        {
            // These are truly invalid on Windows
            invalidPath = "C:\\invalid<>path|with:illegal?chars";
        }
        else
        {
            // On Linux/Unix, use null characters which are always invalid
            invalidPath = "/path/with\0null/char";
        }

        // Act
        var result = BackupValidator.ValidateBackupDirectory(invalidPath);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Error);
        // On some platforms it might be "does not exist" rather than "invalid path"
        result.Message.Should().MatchRegex("(Invalid directory path|does not exist)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenDoesNotExist_ShouldReturnError()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");

        // Act
        var result = BackupValidator.ValidateBackupDirectory(nonExistentPath);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Error);
        result.Message.Should().Contain("does not exist");
        result.Message.Should().Contain(nonExistentPath);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenValidDirectory_ShouldReturnNone()
    {
        // Act
        var result = BackupValidator.ValidateBackupDirectory(_validDirectory);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.None);
        result.Message.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenRelativePath_ShouldResolveAndValidate()
    {
        // Arrange - Create a subdirectory in temp
        var subdir = Path.Combine(_validDirectory, "subdir");
        Directory.CreateDirectory(subdir);
        
        var currentDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_validDirectory);
            
            // Act - Use relative path
            var result = BackupValidator.ValidateBackupDirectory("./subdir");

            // Assert
            result.Severity.Should().Be(ValidationSeverity.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(currentDir);
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("C:\\")]
    [InlineData("D:\\")]
    [InlineData("E:\\")]
    public void ValidateBackupDirectory_WhenWindowsRootDrive_ShouldReturnWarning(string drivePath)
    {
        // This test only applies to Windows
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Only test drives that exist
        if (!Directory.Exists(drivePath))
        {
            return;
        }

        // Act
        var result = BackupValidator.ValidateBackupDirectory(drivePath);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Warning);
        result.Message.Should().Contain("entire system drive");
        result.Message.Should().Contain("not recommended");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenLinuxRoot_ShouldReturnWarning()
    {
        // This test only applies to Linux/Unix
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        // Act
        var result = BackupValidator.ValidateBackupDirectory("/");

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Warning);
        result.Message.Should().Contain("entire system drive");
        result.Message.Should().Contain("not recommended");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("C:\\Windows")]
    [InlineData("C:\\Program Files")]
    [InlineData("C:\\Program Files (x86)")]
    [InlineData("C:\\ProgramData")]
    public void ValidateBackupDirectory_WhenWindowsSystemDirectory_ShouldReturnWarning(string systemPath)
    {
        // This test only applies to Windows
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Only test directories that exist
        if (!Directory.Exists(systemPath))
        {
            return;
        }

        // Act
        var result = BackupValidator.ValidateBackupDirectory(systemPath);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Warning);
        result.Message.Should().Contain("system directory");
        result.Message.Should().Contain(".backupignore");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("/bin")]
    [InlineData("/sbin")]
    [InlineData("/usr")]
    [InlineData("/etc")]
    public void ValidateBackupDirectory_WhenLinuxSystemDirectory_ShouldReturnWarning(string systemPath)
    {
        // This test only applies to Linux/Unix
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        // Only test directories that exist
        if (!Directory.Exists(systemPath))
        {
            return;
        }

        // Act
        var result = BackupValidator.ValidateBackupDirectory(systemPath);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Warning);
        result.Message.Should().Contain("system directory");
        result.Message.Should().Contain(".backupignore");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenUserHomeDirectory_ShouldPass()
    {
        // Arrange
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        
        // Skip if home directory doesn't exist (shouldn't happen, but be safe)
        if (string.IsNullOrEmpty(homeDirectory) || !Directory.Exists(homeDirectory))
        {
            return;
        }

        // Act
        var result = BackupValidator.ValidateBackupDirectory(homeDirectory);

        // Assert
        result.Severity.Should().BeOneOf(ValidationSeverity.None, ValidationSeverity.Warning);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenSubdirectoryOfSystemFolder_ShouldReturnWarning()
    {
        // This test only applies to Windows
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windowsPath = @"C:\Windows";
        if (!Directory.Exists(windowsPath))
        {
            return;
        }

        // Use a subdirectory of Windows
        var systemSubdir = Path.Combine(windowsPath, "System32");
        if (!Directory.Exists(systemSubdir))
        {
            return;
        }

        // Act
        var result = BackupValidator.ValidateBackupDirectory(systemSubdir);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.Warning);
        result.Message.Should().Contain("system directory");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenTrailingSlash_ShouldNormalize()
    {
        // Arrange
        var pathWithSlash = _validDirectory + Path.DirectorySeparatorChar;

        // Act
        var result = BackupValidator.ValidateBackupDirectory(pathWithSlash);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WithMultipleCalls_ShouldBeConsistent()
    {
        // Act - Call validation multiple times
        var result1 = BackupValidator.ValidateBackupDirectory(_validDirectory);
        var result2 = BackupValidator.ValidateBackupDirectory(_validDirectory);
        var result3 = BackupValidator.ValidateBackupDirectory(_validDirectory);

        // Assert - Results should be identical
        result1.Severity.Should().Be(result2.Severity);
        result2.Severity.Should().Be(result3.Severity);
        result1.Message.Should().Be(result2.Message);
        result2.Message.Should().Be(result3.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenDirectoryWithFiles_ShouldValidate()
    {
        // Arrange - Create some files in directory
        File.WriteAllText(Path.Combine(_validDirectory, "test1.txt"), "content");
        File.WriteAllText(Path.Combine(_validDirectory, "test2.txt"), "content");

        // Act
        var result = BackupValidator.ValidateBackupDirectory(_validDirectory);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateBackupDirectory_WhenEmptyDirectory_ShouldValidate()
    {
        // Arrange - Use empty directory (already created in constructor)

        // Act
        var result = BackupValidator.ValidateBackupDirectory(_validDirectory);

        // Assert
        result.Severity.Should().Be(ValidationSeverity.None);
    }

    public void Dispose()
    {
        // Clean up test directories
        try
        {
            if (Directory.Exists(_validDirectory))
            {
                Directory.Delete(_validDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }

        try
        {
            if (Directory.Exists(_restrictedDirectory))
            {
                Directory.Delete(_restrictedDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
