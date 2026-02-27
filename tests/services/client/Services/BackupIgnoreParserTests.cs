using FluentAssertions;
using FlorisDeV.BackupClient.Services;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Tests for BackupIgnoreParser functionality.
/// </summary>
public class BackupIgnoreParserTests : IDisposable
{
    private readonly string _testIgnoreFile;

    public BackupIgnoreParserTests()
    {
        _testIgnoreFile = Path.Combine(Path.GetTempPath(), $"test-ignore-{Guid.NewGuid()}.txt");
    }

    public void Dispose()
    {
        if (File.Exists(_testIgnoreFile))
        {
            File.Delete(_testIgnoreFile);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReadIgnoreFile_WithValidFile_ReturnsPatterns()
    {
        // Arrange
        var content = @"# Comment line
*.tmp
*.log

# Another comment
node_modules/**
.git/**";
        File.WriteAllText(_testIgnoreFile, content);

        // Act
        var patterns = BackupIgnoreParser.ReadIgnoreFile(_testIgnoreFile);

        // Assert
        patterns.Should().NotBeNull();
        patterns.Should().HaveCount(4);
        patterns.Should().Contain("*.tmp");
        patterns.Should().Contain("*.log");
        patterns.Should().Contain("node_modules/**");
        patterns.Should().Contain(".git/**");
        patterns.Should().NotContain(p => p.StartsWith("#"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReadIgnoreFile_WhenFileDoesNotExist_ReturnsNull()
    {
        // Act
        var patterns = BackupIgnoreParser.ReadIgnoreFile("/non/existent/file");

        // Assert
        patterns.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReadIgnoreFile_WithEmptyFile_ReturnsNull()
    {
        // Arrange
        File.WriteAllText(_testIgnoreFile, string.Empty);

        // Act
        var patterns = BackupIgnoreParser.ReadIgnoreFile(_testIgnoreFile);

        // Assert
        patterns.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReadIgnoreFile_WithOnlyCommentsAndEmptyLines_ReturnsNull()
    {
        // Arrange
        var content = @"# Just comments

# More comments

";
        File.WriteAllText(_testIgnoreFile, content);

        // Act
        var patterns = BackupIgnoreParser.ReadIgnoreFile(_testIgnoreFile);

        // Assert
        patterns.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReadIgnoreFile_TrimsWhitespace()
    {
        // Arrange
        var content = @"  *.tmp
   *.log
node_modules/**  ";
        File.WriteAllText(_testIgnoreFile, content);

        // Act
        var patterns = BackupIgnoreParser.ReadIgnoreFile(_testIgnoreFile);

        // Assert
        patterns.Should().NotBeNull();
        patterns.Should().HaveCount(3);
        patterns.Should().AllSatisfy(p => p.Should().NotStartWith(" ").And.NotEndWith(" "));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCombinedPatterns_WithBothFileAndConfig_CombinesBoth()
    {
        // Arrange
        File.WriteAllText(_testIgnoreFile, "*.tmp\n*.log");
        var configPatterns = new[] { "*.bak", "bin/**" };

        // Act
        var combined = BackupIgnoreParser.GetCombinedPatterns(_testIgnoreFile, configPatterns);

        // Assert
        combined.Should().NotBeNull();
        combined.Should().HaveCount(4);
        combined.Should().Contain("*.tmp");
        combined.Should().Contain("*.log");
        combined.Should().Contain("*.bak");
        combined.Should().Contain("bin/**");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCombinedPatterns_WithDuplicates_RemovesDuplicates()
    {
        // Arrange
        File.WriteAllText(_testIgnoreFile, "*.tmp\n*.log");
        var configPatterns = new[] { "*.tmp", "*.bak" }; // *.tmp is duplicate

        // Act
        var combined = BackupIgnoreParser.GetCombinedPatterns(_testIgnoreFile, configPatterns);

        // Assert
        combined.Should().NotBeNull();
        combined.Should().HaveCount(3);
        combined.Should().Contain("*.tmp");
        combined.Should().Contain("*.log");
        combined.Should().Contain("*.bak");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCombinedPatterns_WithOnlyFile_ReturnsFilePatterns()
    {
        // Arrange
        File.WriteAllText(_testIgnoreFile, "*.tmp\n*.log");

        // Act
        var combined = BackupIgnoreParser.GetCombinedPatterns(_testIgnoreFile, null);

        // Assert
        combined.Should().NotBeNull();
        combined.Should().HaveCount(2);
        combined.Should().Contain("*.tmp");
        combined.Should().Contain("*.log");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCombinedPatterns_WithOnlyConfig_ReturnsConfigPatterns()
    {
        // Arrange
        var configPatterns = new[] { "*.bak", "bin/**" };

        // Act
        var combined = BackupIgnoreParser.GetCombinedPatterns(null, configPatterns);

        // Assert
        combined.Should().NotBeNull();
        combined.Should().HaveCount(2);
        combined.Should().Contain("*.bak");
        combined.Should().Contain("bin/**");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCombinedPatterns_WithNeither_ReturnsNull()
    {
        // Act
        var combined = BackupIgnoreParser.GetCombinedPatterns(null, null);

        // Assert
        combined.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCombinedPatterns_WhenFileDoesNotExist_UsesOnlyConfig()
    {
        // Arrange
        var configPatterns = new[] { "*.bak", "bin/**" };

        // Act
        var combined = BackupIgnoreParser.GetCombinedPatterns("/non/existent/file", configPatterns);

        // Assert
        combined.Should().NotBeNull();
        combined.Should().HaveCount(2);
        combined.Should().Contain("*.bak");
        combined.Should().Contain("bin/**");
    }
}
