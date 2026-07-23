using FluentAssertions;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Configuration;

namespace FlorisDeV.BackupClient.Tests.Services;

/// <summary>
/// Tests for the pure config-parsing/merging helpers behind the "configure" CLI command.
/// </summary>
public class ClientSetupServiceTests : IDisposable
{
    private readonly string _localConfigPath;

    public ClientSetupServiceTests()
    {
        _localConfigPath = Path.Combine(Path.GetTempPath(), $"appsettings.local-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_localConfigPath))
        {
            File.Delete(_localConfigPath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseBackupTargets_ReadsConfiguredTargets()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackupClient:BackupTargets:documents"] = "/home/user/documents",
                ["BackupClient:BackupTargets:projects"] = "/home/user/projects"
            })
            .Build();

        var targets = ClientSetupService.ParseBackupTargets(configuration);

        targets.Should().HaveCount(2);
        targets.Should().Contain("documents", "/home/user/documents");
        targets.Should().Contain("projects", "/home/user/projects");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseBackupTargets_WithNoTargetsConfigured_ReturnsEmpty()
    {
        var configuration = new ConfigurationBuilder().Build();

        var targets = ClientSetupService.ParseBackupTargets(configuration);

        targets.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WriteBackupTargets_WhenFileDoesNotExist_CreatesItWithTargets()
    {
        var targets = new Dictionary<string, string> { ["documents"] = "/home/user/documents" };

        ClientSetupService.WriteBackupTargets(_localConfigPath, targets);

        var written = ClientSetupService.ParseBackupTargets(
            new ConfigurationBuilder().AddJsonFile(_localConfigPath).Build());

        written.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("documents", "/home/user/documents"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WriteBackupTargets_PreservesOtherExistingContent()
    {
        File.WriteAllText(_localConfigPath, """
            {
              "Database": { "FilePath": "/home/user/state.db" },
              "BackupClient": { "BackupTargets": { "old": "/old/path" } }
            }
            """);

        ClientSetupService.WriteBackupTargets(_localConfigPath, new Dictionary<string, string>
        {
            ["documents"] = "/home/user/documents"
        });

        var configuration = new ConfigurationBuilder().AddJsonFile(_localConfigPath).Build();

        configuration["Database:FilePath"].Should().Be("/home/user/state.db");
        var targets = ClientSetupService.ParseBackupTargets(configuration);
        targets.Should().ContainSingle();
        targets.Should().Contain("documents", "/home/user/documents");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WriteBackupTargets_OverwritesPreviouslySavedTargets()
    {
        ClientSetupService.WriteBackupTargets(_localConfigPath, new Dictionary<string, string>
        {
            ["old"] = "/old/path"
        });

        ClientSetupService.WriteBackupTargets(_localConfigPath, new Dictionary<string, string>
        {
            ["new"] = "/new/path"
        });

        var targets = ClientSetupService.ParseBackupTargets(
            new ConfigurationBuilder().AddJsonFile(_localConfigPath).Build());

        targets.Should().ContainSingle();
        targets.Should().Contain("new", "/new/path");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("1", new[] { 0 })]
    [InlineData("1,3", new[] { 0, 2 })]
    [InlineData("1, 3 , 2", new[] { 0, 2, 1 })]
    [InlineData("", new int[0])]
    [InlineData(null, new int[0])]
    [InlineData("   ", new int[0])]
    public void ParseSelection_ParsesValidSelections(string? input, int[] expected)
    {
        var result = ClientSetupService.ParseSelection(input, count: 3);

        result.Should().Equal(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseSelection_IgnoresOutOfRangeAndNonNumericTokens()
    {
        var result = ClientSetupService.ParseSelection("0,1,4,abc,2", count: 3);

        result.Should().Equal(0, 1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetSuggestedTargets_OnlyReturnsFoldersThatActuallyExist()
    {
        var suggestions = ClientSetupService.GetSuggestedTargets();

        suggestions.Should().OnlyContain(s => Directory.Exists(s.Path));
        suggestions.Should().OnlyHaveUniqueItems(s => s.Name);
    }
}
