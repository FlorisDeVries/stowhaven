using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupContracts.Manifest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Services;

public interface IBackupEncryptionService
{
    Task<PreparedUpload> PrepareUploadAsync(TaggedFile file, CancellationToken cancellationToken = default);

    Task DecryptFileAsync(
        string encryptedFilePath,
        string destinationFilePath,
        FileEncryptionMetadata encryption,
        CancellationToken cancellationToken = default);
}

public sealed class PreparedUpload(Stream content, TaggedFile file, string? temporaryFilePath = null) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public TaggedFile File { get; } = file;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(temporaryFilePath) && System.IO.File.Exists(temporaryFilePath))
        {
            System.IO.File.Delete(temporaryFilePath);
        }
    }
}

public partial class BackupEncryptionService(
    IFileSystemService fileSystemService,
    IOptions<BackupClientOptions> options,
    ILogger<BackupEncryptionService> logger) : IBackupEncryptionService
{
    private const int MasterKeyBytes = 32;
    private const int FileKeyBytes = 64;
    private const int AesKeyBytes = 32;
    private const int AesBlockBytes = 16;
    private const int AesGcmNonceBytes = 12;
    private const int AesGcmTagBytes = 16;
    private const int RecoveryPhraseTokenCount = 12;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly BackupClientOptions _options = options.Value;
    private RecoveryMaterial? _recoveryMaterial;

    public async Task<PreparedUpload> PrepareUploadAsync(TaggedFile file, CancellationToken cancellationToken = default)
    {
        if (_options.Encryption.Mode == BackupEncryptionMode.ServerSideOnly)
        {
            var stream = await fileSystemService.GetFileStreamAsync(file.Metadata.FilePath, cancellationToken).ConfigureAwait(false);
            return new PreparedUpload(stream, file with
            {
                UploadSha256 = file.Metadata.Hash,
                UploadSizeBytes = file.Metadata.SizeBytes
            });
        }

        if (_options.Encryption.Mode != BackupEncryptionMode.ClientAndServer)
        {
            throw new InvalidOperationException($"Unsupported backup encryption mode: {_options.Encryption.Mode}");
        }

        var material = await GetRecoveryMaterialAsync(cancellationToken).ConfigureAwait(false);
        var masterKey = DeriveMasterKey(material.RecoveryPhrase, material.KdfSalt, material.KdfIterations);

        var fileKey = RandomNumberGenerator.GetBytes(FileKeyBytes);
        var aesKey = fileKey[..AesKeyBytes];
        var hmacKey = fileKey[AesKeyBytes..];
        var iv = RandomNumberGenerator.GetBytes(AesBlockBytes);
        var tempPath = Path.Combine(Path.GetTempPath(), $"backup-encrypted-{Guid.NewGuid():N}.bin");

        await EncryptFileToTemporaryFileAsync(file.Metadata.FilePath, tempPath, aesKey, iv, cancellationToken).ConfigureAwait(false);
        var (ciphertextSha256, ciphertextSize, hmac) = await HashAndMacAsync(tempPath, iv, hmacKey, cancellationToken).ConfigureAwait(false);
        var wrappedKey = WrapFileKey(masterKey, fileKey);

        CryptographicOperations.ZeroMemory(masterKey);
        CryptographicOperations.ZeroMemory(fileKey);

        var encryptedFile = file with
        {
            UploadSha256 = ciphertextSha256,
            UploadSizeBytes = ciphertextSize,
            Encryption = new FileEncryptionMetadata
            {
                Mode = BackupEncryptionMode.ClientAndServer.ToString(),
                Algorithm = "AES-256-CBC-HMAC-SHA256",
                KeyWrapAlgorithm = "AES-256-GCM",
                Kdf = "PBKDF2-SHA256",
                KdfIterations = material.KdfIterations,
                KdfSalt = Convert.ToBase64String(material.KdfSalt),
                Iv = Convert.ToBase64String(iv),
                WrappedKey = wrappedKey,
                AuthenticationTag = Convert.ToBase64String(hmac),
                PlaintextSha256 = file.Metadata.Hash,
                PlaintextSize = file.Metadata.SizeBytes
            }
        };

        var encryptedStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 128, useAsync: true);
        return new PreparedUpload(encryptedStream, encryptedFile, tempPath);
    }

    public async Task DecryptFileAsync(
        string encryptedFilePath,
        string destinationFilePath,
        FileEncryptionMetadata encryption,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(encryption.Mode, BackupEncryptionMode.ClientAndServer.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported encryption mode '{encryption.Mode}' for restore.");
        }

        if (!string.Equals(encryption.Algorithm, "AES-256-CBC-HMAC-SHA256", StringComparison.Ordinal) ||
            !string.Equals(encryption.KeyWrapAlgorithm, "AES-256-GCM", StringComparison.Ordinal) ||
            !string.Equals(encryption.Kdf, "PBKDF2-SHA256", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsupported encryption metadata for restore.");
        }

        var material = await GetExistingRecoveryMaterialAsync(encryption, cancellationToken).ConfigureAwait(false);
        var masterKey = DeriveMasterKey(material.RecoveryPhrase, material.KdfSalt, material.KdfIterations);
        var fileKey = UnwrapFileKey(masterKey, encryption.WrappedKey);
        var aesKey = fileKey[..AesKeyBytes];
        var hmacKey = fileKey[AesKeyBytes..];
        var iv = Convert.FromBase64String(encryption.Iv);

        try
        {
            await VerifyFileHmacAsync(encryptedFilePath, iv, hmacKey, Convert.FromBase64String(encryption.AuthenticationTag), cancellationToken).ConfigureAwait(false);
            await DecryptFileContentsAsync(encryptedFilePath, destinationFilePath, aesKey, iv, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(encryption.PlaintextSha256))
            {
                var plaintextSha256 = await ComputeFileSha256Async(destinationFilePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(plaintextSha256, encryption.PlaintextSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(destinationFilePath);
                    throw new CryptographicException("Restored plaintext SHA-256 verification failed.");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    private async Task<RecoveryMaterial> GetRecoveryMaterialAsync(CancellationToken cancellationToken)
    {
        if (_recoveryMaterial != null)
        {
            return _recoveryMaterial;
        }

        var phrasePath = GetRecoveryPhraseFilePath();
        if (File.Exists(phrasePath))
        {
            await using var existingStream = new FileStream(phrasePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
            var existing = await JsonSerializer.DeserializeAsync<RecoveryPhraseFile>(existingStream, JsonOptions, cancellationToken).ConfigureAwait(false)
                           ?? throw new InvalidOperationException($"Recovery phrase file '{phrasePath}' is empty or invalid.");

            _recoveryMaterial = new RecoveryMaterial(
                NormalizeRecoveryPhrase(existing.RecoveryPhrase),
                Convert.FromBase64String(existing.KdfSalt),
                existing.KdfIterations);

            return _recoveryMaterial;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(phrasePath) ?? AppContext.BaseDirectory);

        var phrase = GenerateRecoveryPhrase();
        var kdfSalt = RandomNumberGenerator.GetBytes(16);
        var phraseFile = new RecoveryPhraseFile
        {
            Mode = BackupEncryptionMode.ClientAndServer.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            RecoveryPhrase = phrase,
            Kdf = "PBKDF2-SHA256",
            KdfIterations = _options.Encryption.KdfIterations,
            KdfSalt = Convert.ToBase64String(kdfSalt),
            Warning = "Write the recovery phrase down and keep it offline. If this file and phrase are lost, encrypted backups cannot be recovered."
        };

        await using (var newStream = new FileStream(phrasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(newStream, phraseFile, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        LogRecoveryPhraseCreated(logger, phrasePath);
        _recoveryMaterial = new RecoveryMaterial(phrase, kdfSalt, phraseFile.KdfIterations);
        return _recoveryMaterial;
    }

    private async Task<RecoveryMaterial> GetExistingRecoveryMaterialAsync(FileEncryptionMetadata encryption, CancellationToken cancellationToken)
    {
        if (_recoveryMaterial != null)
        {
            return _recoveryMaterial;
        }

        var phrasePath = GetRecoveryPhraseFilePath();
        if (!File.Exists(phrasePath))
        {
            throw new FileNotFoundException(
                "Recovery phrase file was not found. Encrypted backups cannot be restored without the recovery phrase.",
                phrasePath);
        }

        await using var existingStream = new FileStream(phrasePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        var existing = await JsonSerializer.DeserializeAsync<RecoveryPhraseFile>(existingStream, JsonOptions, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Recovery phrase file '{phrasePath}' is empty or invalid.");

        _recoveryMaterial = new RecoveryMaterial(
            NormalizeRecoveryPhrase(existing.RecoveryPhrase),
            Convert.FromBase64String(encryption.KdfSalt),
            encryption.KdfIterations);

        return _recoveryMaterial;
    }

    private string GetRecoveryPhraseFilePath()
    {
        if (!string.IsNullOrWhiteSpace(_options.Encryption.RecoveryPhraseFilePath))
        {
            return Environment.ExpandEnvironmentVariables(_options.Encryption.RecoveryPhraseFilePath);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = AppContext.BaseDirectory;
        }

        return Path.Combine(appData, "FlorisDeV", "BackupClient", "recovery-phrase.json");
    }

    private static async Task EncryptFileToTemporaryFileAsync(string sourcePath, string tempPath, byte[] aesKey, byte[] iv, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 128, useAsync: true);
        await using var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1024 * 128, useAsync: true);
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = aesKey;
        aes.IV = iv;

        await using var cryptoStream = new CryptoStream(destination, aes.CreateEncryptor(), CryptoStreamMode.Write);
        await source.CopyToAsync(cryptoStream, cancellationToken).ConfigureAwait(false);
        cryptoStream.FlushFinalBlock();
    }

    private static async Task<(string Sha256, long Size, byte[] Hmac)> HashAndMacAsync(string tempPath, byte[] iv, byte[] hmacKey, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        using var hmac = new HMACSHA256(hmacKey);
        hmac.TransformBlock(iv, 0, iv.Length, null, 0);

        var buffer = new byte[1024 * 128];
        long totalBytes = 0;
        await using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            sha256.TransformBlock(buffer, 0, read, null, 0);
            hmac.TransformBlock(buffer, 0, read, null, 0);
            totalBytes += read;
        }

        sha256.TransformFinalBlock([], 0, 0);
        hmac.TransformFinalBlock([], 0, 0);

        return (Convert.ToHexString(sha256.Hash!).ToLowerInvariant(), totalBytes, hmac.Hash!);
    }

    private static string WrapFileKey(byte[] masterKey, byte[] fileKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcmNonceBytes);
        var ciphertext = new byte[fileKey.Length];
        var tag = new byte[AesGcmTagBytes];

        using var aesGcm = new AesGcm(masterKey, AesGcmTagBytes);
        aesGcm.Encrypt(nonce, fileKey, ciphertext, tag);

        return $"{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(ciphertext)}.{Convert.ToBase64String(tag)}";
    }

    private static byte[] UnwrapFileKey(byte[] masterKey, string wrappedKey)
    {
        var parts = wrappedKey.Split('.', 3);
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("Invalid wrapped file key format.");
        }

        var nonce = Convert.FromBase64String(parts[0]);
        var ciphertext = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var fileKey = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(masterKey, AesGcmTagBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, fileKey);
        return fileKey;
    }

    private static async Task VerifyFileHmacAsync(string filePath, byte[] iv, byte[] hmacKey, byte[] expectedHmac, CancellationToken cancellationToken)
    {
        var (_, _, actualHmac) = await HashAndMacAsync(filePath, iv, hmacKey, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actualHmac, expectedHmac))
        {
            throw new CryptographicException("Encrypted file HMAC verification failed.");
        }
    }

    private static async Task DecryptFileContentsAsync(string encryptedFilePath, string destinationFilePath, byte[] aesKey, byte[] iv, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath) ?? AppContext.BaseDirectory);

        await using var source = new FileStream(encryptedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 128, useAsync: true);
        await using var destination = new FileStream(destinationFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1024 * 128, useAsync: true);
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = aesKey;
        aes.IV = iv;

        await using var cryptoStream = new CryptoStream(source, aes.CreateDecryptor(), CryptoStreamMode.Read);
        await cryptoStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 128, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] DeriveMasterKey(string recoveryPhrase, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(NormalizeRecoveryPhrase(recoveryPhrase)),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            MasterKeyBytes);

    private static string GenerateRecoveryPhrase()
    {
        Span<byte> entropy = stackalloc byte[RecoveryPhraseTokenCount * 2];
        RandomNumberGenerator.Fill(entropy);

        var tokens = new string[RecoveryPhraseTokenCount];
        for (var i = 0; i < RecoveryPhraseTokenCount; i++)
        {
            var value = ((entropy[i * 2] << 8) | entropy[(i * 2) + 1]) & 0x0fff;
            tokens[i] = $"{Adjectives[value >> 6]}-{Nouns[value & 0x3f]}";
        }

        return string.Join(' ', tokens);
    }

    private static string NormalizeRecoveryPhrase(string phrase)
        => string.Join(' ', phrase.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();

    [LoggerMessage(LogLevel.Warning, "Created zero-knowledge backup recovery phrase file at {path}. Write the phrase down offline; without it, encrypted backups cannot be recovered.")]
    static partial void LogRecoveryPhraseCreated(ILogger logger, string path);

    private sealed record RecoveryMaterial(string RecoveryPhrase, byte[] KdfSalt, int KdfIterations);

    private sealed class RecoveryPhraseFile
    {
        public required string Mode { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required string RecoveryPhrase { get; init; }
        public required string Kdf { get; init; }
        public required int KdfIterations { get; init; }
        public required string KdfSalt { get; init; }
        public required string Warning { get; init; }
    }

    private static readonly string[] Adjectives =
    [
        "able", "amber", "ancient", "arctic", "autumn", "bold", "bright", "calm",
        "cedar", "clear", "clever", "cobalt", "cosmic", "crimson", "daring", "dawn",
        "deep", "desert", "eager", "ember", "fable", "fair", "fierce", "forest",
        "gentle", "golden", "grand", "green", "harbor", "hidden", "honest", "ivory",
        "jolly", "kind", "lively", "lunar", "maple", "misty", "noble", "north",
        "ocean", "opal", "patient", "polar", "quiet", "rapid", "raven", "river",
        "royal", "silver", "solar", "steady", "stone", "summer", "swift", "timber",
        "true", "urban", "velvet", "violet", "warm", "western", "wild", "winter"
    ];

    private static readonly string[] Nouns =
    [
        "anchor", "apple", "arrow", "bison", "bridge", "brook", "canyon", "castle",
        "circle", "cloud", "comet", "copper", "coral", "dolphin", "dragon", "eagle",
        "earth", "falcon", "field", "flame", "flower", "forest", "galaxy", "garden",
        "harbor", "hazel", "island", "jacket", "jungle", "kitten", "ladder", "lantern",
        "meadow", "meteor", "mirror", "mountain", "nebula", "night", "orange", "orchid",
        "panda", "pearl", "planet", "prairie", "quartz", "rabbit", "rocket", "sailor",
        "shadow", "signal", "sparrow", "spirit", "stream", "summit", "thunder", "tiger",
        "valley", "violet", "voyage", "walnut", "whisper", "willow", "window", "zephyr"
    ];
}
