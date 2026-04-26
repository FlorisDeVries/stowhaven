using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FluentAssertions;

namespace FlorisDeV.BackupClient.Tests.Services;

public class BackupEncryptionServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"backup-encryption-tests-{Guid.NewGuid():N}");
    private readonly Mock<IFileSystemService> _fileSystem = new();
    private readonly Mock<ILogger<BackupEncryptionService>> _logger = new();

    public BackupEncryptionServiceTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PrepareUploadAsync_WhenServerSideOnly_ShouldUseOriginalStreamAndMetadata()
    {
        // Arrange
        var fileBytes = Encoding.UTF8.GetBytes("plain text");
        var sourceStream = new MemoryStream(fileBytes);
        var taggedFile = CreateTaggedFile("plain.txt", fileBytes);
        var phraseFilePath = Path.Combine(_tempDirectory, "recovery-phrase.json");
        var sut = CreateSut(BackupEncryptionMode.ServerSideOnly, phraseFilePath);

        _fileSystem.Setup(x => x.GetFileStreamAsync(taggedFile.Metadata.FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceStream);

        // Act
        await using var prepared = await sut.PrepareUploadAsync(taggedFile, CancellationToken.None);

        // Assert
        prepared.Content.Should().BeSameAs(sourceStream);
        prepared.File.UploadSha256.Should().Be(taggedFile.Metadata.Hash);
        prepared.File.UploadSizeBytes.Should().Be(taggedFile.Metadata.SizeBytes);
        prepared.File.Encryption.Should().BeNull();
        File.Exists(phraseFilePath).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PrepareUploadAsync_WhenClientAndServer_ShouldCreatePhraseFileAndReturnEncryptedUpload()
    {
        // Arrange
        var fileBytes = Encoding.UTF8.GetBytes("secret document");
        var taggedFile = CreateTaggedFile("secret.txt", fileBytes);
        var phraseFilePath = Path.Combine(_tempDirectory, "recovery-phrase.json");
        var sut = CreateSut(BackupEncryptionMode.ClientAndServer, phraseFilePath);

        // Act
        await using var prepared = await sut.PrepareUploadAsync(taggedFile, CancellationToken.None);
        using var encryptedBytes = new MemoryStream();
        await prepared.Content.CopyToAsync(encryptedBytes);

        // Assert
        File.Exists(phraseFilePath).Should().BeTrue();
        prepared.File.Encryption.Should().NotBeNull();
        prepared.File.Encryption!.Mode.Should().Be(nameof(BackupEncryptionMode.ClientAndServer));
        prepared.File.Encryption.Algorithm.Should().Be("AES-256-CBC-HMAC-SHA256");
        prepared.File.Encryption.KeyWrapAlgorithm.Should().Be("AES-256-GCM");
        prepared.File.Encryption.Kdf.Should().Be("PBKDF2-SHA256");
        prepared.File.Encryption.KdfIterations.Should().Be(1_000);
        prepared.File.Encryption.PlaintextSha256.Should().Be(taggedFile.Metadata.Hash);
        prepared.File.Encryption.PlaintextSize.Should().Be(taggedFile.Metadata.SizeBytes);
        prepared.File.UploadSha256.Should().NotBe(taggedFile.Metadata.Hash);
        prepared.File.UploadSizeBytes.Should().Be(encryptedBytes.Length);
        encryptedBytes.ToArray().Should().NotEqual(fileBytes);

        using var phraseJson = JsonDocument.Parse(await File.ReadAllTextAsync(phraseFilePath));
        var phrase = phraseJson.RootElement.GetProperty("recoveryPhrase").GetString();
        phrase.Should().NotBeNullOrWhiteSpace();
        phrase!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(12);
        phraseJson.RootElement.GetProperty("warning").GetString().Should().Contain("cannot be recovered");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PrepareUploadAsync_WhenClientAndServerPhraseFileExists_ShouldReusePhraseFile()
    {
        // Arrange
        var phraseFilePath = Path.Combine(_tempDirectory, "recovery-phrase.json");
        var sut = CreateSut(BackupEncryptionMode.ClientAndServer, phraseFilePath);
        var firstFile = CreateTaggedFile("first.txt", Encoding.UTF8.GetBytes("first"));
        var secondFile = CreateTaggedFile("second.txt", Encoding.UTF8.GetBytes("second"));

        await using (var firstPrepared = await sut.PrepareUploadAsync(firstFile, CancellationToken.None))
        {
        }

        var originalPhraseFile = await File.ReadAllTextAsync(phraseFilePath);

        // Act
        await using (var secondPrepared = await sut.PrepareUploadAsync(secondFile, CancellationToken.None))
        {
        }

        // Assert
        var currentPhraseFile = await File.ReadAllTextAsync(phraseFilePath);
        currentPhraseFile.Should().Be(originalPhraseFile);
    }

    private BackupEncryptionService CreateSut(BackupEncryptionMode mode, string phraseFilePath) => new(
        _fileSystem.Object,
        Options.Create(new BackupClientOptions
        {
            BackupTargets = new Dictionary<string, string> { ["documents"] = _tempDirectory },
            Encryption = new BackupEncryptionOptions
            {
                Mode = mode,
                RecoveryPhraseFilePath = phraseFilePath,
                KdfIterations = 1_000
            }
        }),
        _logger.Object);

    private TaggedFile CreateTaggedFile(string fileName, byte[] content)
    {
        var filePath = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(filePath, content);

        return new TaggedFile(
            "documents",
            _tempDirectory,
            new FileMetadata(filePath, content.Length, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ComputeSha256(content)))
        {
            UniqueFileId = $"{Path.GetFileNameWithoutExtension(fileName)}-unique"
        };
    }

    private static string ComputeSha256(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
