using System.Security;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using FlorisDeV.BackupApi.Models.Infrastructure;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using Microsoft.Extensions.Logging;
using Moq;

namespace FlorisDeV.BackupApi.Tests;

/// <summary>
/// Tests for SasUrlService to verify SAS URL generation and security-critical path validation.
/// </summary>
public class SasUrlServiceTests
{
    private readonly Mock<ILogger<SasUrlService>> _loggerMock;
    private readonly Mock<ISecretService> _secretServiceMock;
    private readonly Mock<TelemetryProvider> _telemetryMock;
    private readonly SasUrlService _sut;

    public SasUrlServiceTests()
    {
        _loggerMock = new Mock<ILogger<SasUrlService>>();
        _secretServiceMock = new Mock<ISecretService>();
        _telemetryMock = new Mock<TelemetryProvider>();

        _sut = new SasUrlService(_loggerMock.Object, _secretServiceMock.Object, _telemetryMock.Object);

        // Setup default secret values for Azurite (local dev)
        _secretServiceMock.Setup(x => x.GetRequiredSecretAsync("DATA_STORAGE_ACCOUNT"))
            .ReturnsAsync("devstorageaccount1");
        _secretServiceMock.Setup(x => x.GetRequiredSecretAsync("DATA_CONTAINER"))
            .ReturnsAsync("backups");
        _secretServiceMock.Setup(x => x.GetRequiredSecretAsync("USE_AZURITE"))
            .ReturnsAsync("true");
        _secretServiceMock.Setup(x => x.GetRequiredSecretAsync("DATA_STORAGE_ACCOUNT_KEY"))
            .ReturnsAsync("Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==");
        _secretServiceMock.Setup(x => x.GetRequiredSecretAsync("DATA_STORAGE_BLOB_ENDPOINT"))
            .ReturnsAsync("http://127.0.0.1:10000/devstorageaccount1");
    }

    #region Path Validation Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithValidStagingPath_Succeeds()
    {
        // Arrange
        var validPath = "staging/device123/run456";

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(validPath);

        // Assert
        result.Should().NotBeNull();
        result.Url.Should().NotBeNull();
        result.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        result.TtlMinutes.Should().Be(60);
    }

    [Theory]
    [InlineData("staging/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("staging/device-123/run-456")]
    [InlineData("staging/ABC/DEF")]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithValidStagingPaths_Succeeds(string path)
    {
        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(path);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithLeadingSlash_TrimsAndSucceeds()
    {
        // Arrange
        var pathWithSlash = "/staging/device123/run456";

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(pathWithSlash);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithTrailingSlash_TrimsAndSucceeds()
    {
        // Arrange
        var pathWithSlash = "staging/device123/run456/";

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(pathWithSlash);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithCustomTtl_UsesProvidedValue()
    {
        // Arrange
        var path = "staging/device123/run456";
        var ttl = 120;

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(path, ttl);

        // Assert
        result.TtlMinutes.Should().Be(ttl);
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(ttl), TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Security Tests - Path Traversal

    [Theory]
    [InlineData("staging/../secrets")]
    [InlineData("staging/device/../../../etc/passwd")]
    [InlineData("staging/device/../../admin")]
    [InlineData("../staging/device/run")]
    [InlineData("staging/device/../run")]
    [Trait("Category", "Unit")]
    [Trait("Category", "Security")]
    public async Task GenerateUploadSasUrlAsync_WithPathTraversal_ThrowsSecurityException(string maliciousPath)
    {
        // Act
        var act = async () => await _sut.GenerateUploadSasUrlAsync(maliciousPath);

        // Assert
        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*path traversal*");
    }

    #endregion

    #region Security Tests - Staging Enforcement

    [Theory]
    [InlineData("production/device123/run456")]
    [InlineData("backups/device123/run456")]
    [InlineData("admin/device123/run456")]
    [InlineData("device123/run456")]
    [InlineData("/device123/run456")]
    [Trait("Category", "Unit")]
    [Trait("Category", "Security")]
    public async Task GenerateUploadSasUrlAsync_WithNonStagingPath_ThrowsSecurityException(string nonStagingPath)
    {
        // Act
        var act = async () => await _sut.GenerateUploadSasUrlAsync(nonStagingPath);

        // Assert
        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*staging/*");
    }

    #endregion

    #region Security Tests - Directory vs Blob

    [Theory]
    [InlineData("staging/device123/file.txt")]
    [InlineData("staging/device123/run456/backup.zip")]
    [InlineData("staging/device123/run456/data.json")]
    [InlineData("staging/device123/config.yml")]
    [Trait("Category", "Unit")]
    [Trait("Category", "Security")]
    public async Task GenerateUploadSasUrlAsync_WithBlobPath_ThrowsSecurityException(string blobPath)
    {
        // Act
        var act = async () => await _sut.GenerateUploadSasUrlAsync(blobPath);

        // Assert
        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*directory*");
    }

    #endregion

    #region Invalid Input Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithNullOrWhitespacePath_ThrowsArgumentException(string? invalidPath)
    {
        // Act
        var act = async () => await _sut.GenerateUploadSasUrlAsync(invalidPath!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region TTL Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithNoTtl_UsesDefault60Minutes()
    {
        // Arrange
        var path = "staging/device123/run456";

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(path);

        // Assert
        result.TtlMinutes.Should().Be(60);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(240)]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithVariousTtls_GeneratesCorrectExpiry(int ttl)
    {
        // Arrange
        var path = "staging/device123/run456";

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(path, ttl);

        // Assert
        result.TtlMinutes.Should().Be(ttl);
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(ttl), TimeSpan.FromSeconds(5));
    }

    #endregion

    #region URL Format Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_GeneratesHttpsUrl()
    {
        // Arrange
        var path = "staging/device123/run456";

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(path);

        // Assert
        result.Url.Scheme.Should().Match(s => s == "http" || s == "https");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_UrlContainsPath()
    {
        // Arrange
        var deviceId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");
        var path = $"staging/{deviceId}/{runId}";

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(path);

        // Assert
        result.Url.ToString().Should().Contain("staging");
        result.Url.ToString().Should().Contain("backups"); // container name
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_UrlContainsSasToken()
    {
        // Arrange
        var path = "staging/device123/run456";

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(path);

        // Assert
        result.Url.Query.Should().NotBeNullOrEmpty("SAS URL should have query parameters");
        result.Url.Query.Should().Contain("sig=", "SAS signature should be present");
    }

    #endregion

    #region Edge Cases

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithVeryLongValidPath_Succeeds()
    {
        // Arrange
        var longPath = "staging/" + new string('a', 200) + "/" + new string('b', 200);

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(longPath);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithGuidFormatPath_Succeeds()
    {
        // Arrange
        var deviceId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");
        var path = $"staging/{deviceId}/{runId}";

        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(path);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_CaseInsensitiveStaging_ThrowsSecurityException()
    {
        // Arrange - uppercase STAGING should not bypass security check
        var path = "STAGING/device123/run456";

        // Act
        var act = async () => await _sut.GenerateUploadSasUrlAsync(path);

        // Assert
        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*staging/*");
    }

    [Theory]
    [InlineData("staging//device123/run456")]
    [InlineData("staging/device123//run456")]
    [InlineData("staging///device123///run456")]
    [Trait("Category", "Unit")]
    public async Task GenerateUploadSasUrlAsync_WithDoubleSlashes_Succeeds(string path)
    {
        // Act
        var result = await _sut.GenerateUploadSasUrlAsync(path);

        // Assert
        result.Should().NotBeNull();
    }

    #endregion
}
