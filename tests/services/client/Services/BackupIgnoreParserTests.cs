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
    public void GetCombinedPatterns_WithValidFile_ReturnsFilePatterns()
    {
        // Arrange
        File.WriteAllText(_testIgnoreFile, "*.tmp\n*.log");

        // Act
        var combined = BackupIgnoreParser.GetCombinedPatterns(_testIgnoreFile);

        // Assert
        combined.Should().NotBeNull();
        combined.Should().HaveCount(2);
        combined.Should().Contain("*.tmp");
        combined.Should().Contain("*.log");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCombinedPatterns_WithNullPath_ReturnsNull()
    {
        // Act
        var combined = BackupIgnoreParser.GetCombinedPatterns(null);

        // Assert
        combined.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCombinedPatterns_WhenFileDoesNotExist_ReturnsNull()
    {
        // Act
        var combined = BackupIgnoreParser.GetCombinedPatterns("/non/existent/file");

        // Assert
        combined.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCombinedPatterns_WithRelativePath_ResolvesAgainstExecutableDirectory()
    {
        // Arrange: a relative filename should resolve next to the executable, not the
        // process's current working directory, so it's found regardless of how the
        // scheduler/service launches the process.
        var relativeFileName = $"relative-ignore-{Guid.NewGuid():N}.txt";
        var absolutePath = Path.Combine(AppContext.BaseDirectory, relativeFileName);
        File.WriteAllText(absolutePath, "*.tmp");

        try
        {
            // Act
            var combined = BackupIgnoreParser.GetCombinedPatterns(relativeFileName);

            // Assert
            combined.Should().NotBeNull();
            combined.Should().Contain("*.tmp");
        }
        finally
        {
            File.Delete(absolutePath);
        }
    }
}
