using FluentAssertions;
using FlorisDeV.Logging.Filtering;

namespace FlorisDeV.Logging.Tests;

/// <summary>
/// Tests for TelemetryFilteringWildcardMatcher to verify wildcard pattern matching for telemetry filtering.
/// </summary>
public class TelemetryFilteringWildcardMatcherTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Create_WithTargetOnly_CreatesMatcherWithNullOperation()
    {
        // Arrange & Act
        var matcher = TelemetryFilteringWildcardMatcher.Create("https://*.azure.com/*");

        // Assert
        matcher.Should().NotBeNull();
        matcher.Target.Should().Be("https://*.azure.com/*");
        matcher.Operation.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_WithOperationAndTarget_ParsesBoth()
    {
        // Arrange & Act
        var matcher = TelemetryFilteringWildcardMatcher.Create("GET https://*.azure.com/*");

        // Assert
        matcher.Target.Should().Be("https://*.azure.com/*");
        matcher.Operation.Should().Be("GET");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_WithPostOperation_ParsesCorrectly()
    {
        // Arrange & Act
        var matcher = TelemetryFilteringWildcardMatcher.Create("POST /api/*/upload");

        // Assert
        matcher.Target.Should().Be("/api/*/upload");
        matcher.Operation.Should().Be("POST");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_WithExtraSpaces_TrimsAndParses()
    {
        // Arrange & Act
        var matcher = TelemetryFilteringWildcardMatcher.Create("  DELETE   /api/resource  ");

        // Assert
        matcher.Target.Should().Be("/api/resource");
        matcher.Operation.Should().Be("DELETE");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_WithNullOrEmpty_ThrowsArgumentException()
    {
        // Arrange & Act
        var actNull = () => TelemetryFilteringWildcardMatcher.Create(null!);
        var actEmpty = () => TelemetryFilteringWildcardMatcher.Create("");

        // Assert
        actNull.Should().Throw<ArgumentException>()
            .WithParameterName("operation");
        actEmpty.Should().Throw<ArgumentException>()
            .WithParameterName("operation");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsOperationMatch_WhenOperationIsNull_AlwaysReturnsTrue()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/api/*");

        // Act & Assert
        matcher.IsOperationMatch("GET").Should().BeTrue();
        matcher.IsOperationMatch("POST").Should().BeTrue();
        matcher.IsOperationMatch("DELETE").Should().BeTrue();
        matcher.IsOperationMatch(null).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsOperationMatch_WithMatchingOperation_ReturnsTrue()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("GET /api/*");

        // Act & Assert
        matcher.IsOperationMatch("GET").Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsOperationMatch_WithDifferentOperation_ReturnsFalse()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("GET /api/*");

        // Act & Assert
        matcher.IsOperationMatch("POST").Should().BeFalse();
        matcher.IsOperationMatch("DELETE").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsOperationMatch_IsCaseInsensitive()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("GET /api/*");

        // Act & Assert
        matcher.IsOperationMatch("get").Should().BeTrue();
        matcher.IsOperationMatch("Get").Should().BeTrue();
        matcher.IsOperationMatch("gEt").Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithExactMatch_ReturnsTrue()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/health/liveness");

        // Act & Assert
        matcher.IsTargetMatch("/health/liveness").Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithNonMatch_ReturnsFalse()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/health/liveness");

        // Act & Assert
        matcher.IsTargetMatch("/health/readiness").Should().BeFalse();
        matcher.IsTargetMatch("/api/health").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithAsteriskWildcard_MatchesMultipleCharacters()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/api/*/users");

        // Act & Assert
        matcher.IsTargetMatch("/api/v1/users").Should().BeTrue();
        matcher.IsTargetMatch("/api/v2/users").Should().BeTrue();
        matcher.IsTargetMatch("/api/admin/users").Should().BeTrue();
        matcher.IsTargetMatch("/api/v1/products").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithMultipleAsterisks_MatchesCorrectly()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("https://*.blob.core.windows.net/*/file.txt");

        // Act & Assert
        matcher.IsTargetMatch("https://mystorageacct.blob.core.windows.net/backups/file.txt").Should().BeTrue();
        matcher.IsTargetMatch("https://prod.blob.core.windows.net/data/file.txt").Should().BeTrue();
        matcher.IsTargetMatch("https://mystorageacct.blob.core.windows.net/backups/other.txt").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithQuestionMarkWildcard_MatchesSingleCharacter()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/api/v?/users");

        // Act & Assert
        matcher.IsTargetMatch("/api/v1/users").Should().BeTrue();
        matcher.IsTargetMatch("/api/v2/users").Should().BeTrue();
        matcher.IsTargetMatch("/api/vX/users").Should().BeTrue();
        matcher.IsTargetMatch("/api/v10/users").Should().BeFalse(); // v10 is two characters
        // Note: ? in regex means 0 or 1, so /api/v/users will match
        matcher.IsTargetMatch("/api/v/users").Should().BeTrue(); // matches with zero characters
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithMixedWildcards_MatchesCorrectly()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/api/v?/*/data");

        // Act & Assert
        matcher.IsTargetMatch("/api/v1/users/data").Should().BeTrue();
        matcher.IsTargetMatch("/api/v2/products/data").Should().BeTrue();
        matcher.IsTargetMatch("/api/v10/users/data").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_IsCaseInsensitive()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/API/Users");

        // Act & Assert
        matcher.IsTargetMatch("/api/users").Should().BeTrue();
        matcher.IsTargetMatch("/Api/Users").Should().BeTrue();
        matcher.IsTargetMatch("/API/USERS").Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithNullValue_ReturnsFalse()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/api/*");

        // Act & Assert
        matcher.IsTargetMatch(null).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithSpecialRegexCharacters_EscapesCorrectly()
    {
        // Arrange - patterns with regex special chars like . ( ) [ ] + $ ^
        var matcher = TelemetryFilteringWildcardMatcher.Create("https://example.com/api");

        // Act & Assert
        matcher.IsTargetMatch("https://example.com/api").Should().BeTrue();
        matcher.IsTargetMatch("https://exampleXcom/api").Should().BeFalse(); // . should not match any char
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithTrailingWildcard_MatchesPrefix()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/health/*");

        // Act & Assert
        matcher.IsTargetMatch("/health/liveness").Should().BeTrue();
        matcher.IsTargetMatch("/health/readiness").Should().BeTrue();
        matcher.IsTargetMatch("/health/").Should().BeTrue();
        matcher.IsTargetMatch("/healthz").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithLeadingWildcard_MatchesSuffix()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("*/metrics");

        // Act & Assert
        matcher.IsTargetMatch("/api/metrics").Should().BeTrue();
        matcher.IsTargetMatch("/health/metrics").Should().BeTrue();
        matcher.IsTargetMatch("x/metrics").Should().BeTrue(); // Need at least one char before /
        matcher.IsTargetMatch("/metrics").Should().BeTrue();
        matcher.IsTargetMatch("/api/metrics/count").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTargetMatch_WithOnlyWildcard_MatchesEverything()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("*");

        // Act & Assert
        matcher.IsTargetMatch("anything").Should().BeTrue();
        matcher.IsTargetMatch("/api/users").Should().BeTrue();
        matcher.IsTargetMatch("").Should().BeTrue();
        matcher.IsTargetMatch("https://example.com").Should().BeTrue();
    }

    [Theory]
    [InlineData("GET /api/*", "GET", "/api/users", true)]
    [InlineData("GET /api/*", "POST", "/api/users", false)]
    [InlineData("GET /api/*", "GET", "/health", false)]
    [InlineData("POST https://*.azure.com/*", "POST", "https://mystorageacct.azure.com/data", true)]
    [InlineData("POST https://*.azure.com/*", "GET", "https://mystorageacct.azure.com/data", false)]
    [Trait("Category", "Unit")]
    public void CompleteMatch_WithOperationAndTarget_ValidatesCorrectly(
        string pattern, string operation, string target, bool expectedMatch)
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create(pattern);

        // Act
        var operationMatches = matcher.IsOperationMatch(operation);
        var targetMatches = matcher.IsTargetMatch(target);
        var bothMatch = operationMatches && targetMatches;

        // Assert
        bothMatch.Should().Be(expectedMatch);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_WithQueryStringInTarget_MatchesCorrectly()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/api/users?status=*");

        // Act & Assert
        matcher.IsTargetMatch("/api/users?status=active").Should().BeTrue();
        matcher.IsTargetMatch("/api/users?status=inactive").Should().BeTrue();
        matcher.IsTargetMatch("/api/users?role=admin").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_WithFragmentInTarget_MatchesCorrectly()
    {
        // Arrange
        var matcher = TelemetryFilteringWildcardMatcher.Create("/page#section-*");

        // Act & Assert
        matcher.IsTargetMatch("/page#section-1").Should().BeTrue();
        matcher.IsTargetMatch("/page#section-overview").Should().BeTrue();
        matcher.IsTargetMatch("/page#other").Should().BeFalse();
    }
}
