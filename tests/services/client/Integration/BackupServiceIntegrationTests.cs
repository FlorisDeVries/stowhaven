using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupClient.Services;
using FlorisDeV.BackupClient.Telemetry;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Azure.Storage.Blobs;

namespace FlorisDeV.BackupClient.Tests.Integration;

/// <summary>
/// Integration tests for BackupService that test the full backup flow with real components.
/// Uses temporary file system directories and in-memory SQLite for state management.
/// </summary>
public class BackupServiceIntegrationTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IBackupApiClient> _mockApiClient;
    private readonly TestFileUploader _testUploader;
    private readonly TelemetryProvider _telemetry;

    public BackupServiceIntegrationTests()
    {
        // Create temporary directory for test files
        _testRoot = Path.Combine(Path.GetTempPath(), "BackupServiceIntTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testRoot);

        _mockApiClient = new Mock<IBackupApiClient>();
        _testUploader = new TestFileUploader();
        _telemetry = new TelemetryProvider();

        SetupMockApiClient();
    }

    private void SetupMockApiClient()
    {
        _mockApiClient
            .Setup(x => x.RegisterDevice(It.IsAny<RegisterDeviceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RegisterDeviceRequest req, CancellationToken ct) => new DeviceRegistrationResponse
            {
                DeviceId = req.DeviceId ?? Guid.NewGuid(),
                TenantId = "test-tenant",
                UserId = "test-user",
                DisplayName = req.DisplayName,
                Status = DeviceRegistrationStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });

        // Mock StartBackupRun
        _mockApiClient
            .Setup(x => x.StartBackupRun(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid deviceId, CancellationToken ct) =>
            {
                return new StartBackupRunResponse
                {
                    DeviceId = deviceId,
                    RunId = Guid.NewGuid(),
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = BackupRunStatus.Processing,
                    SasUrlInfo = new SasUrlInfo
                    {
                        Url = new Uri("https://storage.blob.core.windows.net/backups?sas-token"),
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                        TtlMinutes = 60
                    },
                    ManifestSasUrlInfo = new SasUrlInfo
                    {
                        Url = new Uri("https://storage.blob.core.windows.net/backups?sas-token"),
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                        TtlMinutes = 60,
                        BasePath = $"runs/{deviceId:N}/{Guid.NewGuid():N}/"
                    }
                };
            });

        // Mock CommitBackupRun
        _mockApiClient
            .Setup(x => x.CommitBackupRun(It.IsAny<Guid>(), It.IsAny<CommitBackupRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid deviceId, CommitBackupRunRequest req, CancellationToken ct) => new CommitBackupRunResponse
            {
                CommitId = Guid.NewGuid(),
                DeviceId = deviceId,
                RunId = req.RunId,
                Status = CommitJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            });

        _mockApiClient
            .Setup(x => x.GetCommitStatus(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid deviceId, Guid commitId, CancellationToken ct) => new CommitStatusResponse
            {
                DeviceId = deviceId,
                CommitId = commitId,
                Status = CommitJobStatus.Succeeded,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            });
    }

    /// <summary>
    /// Test implementation of IFileUploader that simulates successful uploads without Azure SDK.
    /// </summary>
    private class TestFileUploader : IFileUploader
    {
        public List<string> UploadedPaths { get; } = new();
        public List<string> UploadedManifests { get; } = new();

        public void SetBasePath(string? basePath, bool isPathEmbedded = false)
        {

        }

        public Task UploadRunManifestAsync(
            BlobContainerClient containerClient,
            RunManifest manifest,
            string? basePath,
            bool isPathEmbedded,
            CancellationToken cancellationToken)
        {
            UploadedManifests.Add(isPathEmbedded || string.IsNullOrWhiteSpace(basePath)
                ? "run-manifest.json"
                : $"{basePath.TrimEnd('/')}/run-manifest.json");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TaggedFile>> UploadFilesAsync(
            BlobContainerClient containerClient,
            IReadOnlyList<TaggedFile> files,
            CancellationToken cancellationToken)
        {
            // Simulate successful uploads and track them
            foreach (var file in files)
            {
                UploadedPaths.Add(file.GetStoragePath());
            }
            return Task.FromResult<IReadOnlyList<TaggedFile>>(files);
        }
    }

    private BackupService CreateBackupService(Dictionary<string, string> backupTargets)
    {
        var options = Options.Create(new BackupClientOptions
        {
            BackupTargets = backupTargets,
            MaxParallelUploads = 2,
            MaxFailurePercentage = 10,
            MaxRetryAttempts = 2,
            RetryDelayMs = 100,
            MaxRetryDelayMs = 1000
        });

        // Use unique in-memory database for each BackupService instance
        var dbPath = $"file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        var dbOptions = Options.Create(new DatabaseOptions
        {
            FilePath = dbPath
        });

        var fileSystemService = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var stateService = new BackupStateService(dbOptions, NullLogger<BackupStateService>.Instance);
        var scanner = new BackupScanner(
            fileSystemService,
            stateService,
            NullLogger<BackupScanner>.Instance);

        return new BackupService(
            NullLogger<BackupService>.Instance,
            _telemetry,
            _mockApiClient.Object,
            stateService,
            scanner,
            _testUploader, // Use test uploader instead of real one
            options);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_FirstRun_ShouldBackupAllFiles()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target1");
        Directory.CreateDirectory(targetDir);

        await File.WriteAllTextAsync(Path.Combine(targetDir, "file1.txt"), "Content 1");
        await File.WriteAllTextAsync(Path.Combine(targetDir, "file2.txt"), "Content 2");

        var subDir = Path.Combine(targetDir, "subdir");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "file3.txt"), "Content 3");

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var backupService = CreateBackupService(backupTargets);

        // Act
        var result = await backupService.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().HaveCount(3);
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains("file1.txt"));
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains("file2.txt"));
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains("file3.txt"));

        _mockApiClient.Verify(x => x.StartBackupRun(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockApiClient.Verify(x => x.CommitBackupRun(
            It.IsAny<Guid>(),
            It.IsAny<CommitBackupRunRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_SecondRunNoChanges_ShouldReturnTrueWithoutUploading()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target2");
        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "file1.txt"), "Content 1");

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var backupService = CreateBackupService(backupTargets);

        // First backup
        await backupService.Backup(CancellationToken.None);
        _testUploader.UploadedPaths.Clear();
        _mockApiClient.Invocations.Clear();

        // Act - Second backup with no changes
        var result = await backupService.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().BeEmpty("no files should be uploaded when nothing changed");

        _mockApiClient.Verify(x => x.StartBackupRun(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _mockApiClient.Verify(x => x.CommitBackupRun(
            It.IsAny<Guid>(),
            It.IsAny<CommitBackupRunRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_IncrementalWithModifiedFile_ShouldBackupOnlyModifiedFile()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target3");
        Directory.CreateDirectory(targetDir);
        var file1Path = Path.Combine(targetDir, "file1.txt");
        var file2Path = Path.Combine(targetDir, "file2.txt");

        await File.WriteAllTextAsync(file1Path, "Content 1");
        await File.WriteAllTextAsync(file2Path, "Content 2");

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var backupService = CreateBackupService(backupTargets);

        // First backup
        await backupService.Backup(CancellationToken.None);
        _testUploader.UploadedPaths.Clear();

        // Modify one file
        await Task.Delay(10); // Ensure different timestamp
        await File.WriteAllTextAsync(file1Path, "Modified Content 1");

        // Act - Second backup
        var result = await backupService.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().ContainSingle();
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains("file1.txt"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_WithNewFile_ShouldBackupOnlyNewFile()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target4");
        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "file1.txt"), "Content 1");

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var backupService = CreateBackupService(backupTargets);

        // First backup
        await backupService.Backup(CancellationToken.None);
        _testUploader.UploadedPaths.Clear();

        // Add new file
        await File.WriteAllTextAsync(Path.Combine(targetDir, "file2.txt"), "Content 2");

        // Act - Second backup
        var result = await backupService.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().ContainSingle();
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains("file2.txt"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_WithDeletedFile_ShouldDetectDeletion()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target5");
        Directory.CreateDirectory(targetDir);
        var file1Path = Path.Combine(targetDir, "file1.txt");
        var file2Path = Path.Combine(targetDir, "file2.txt");

        await File.WriteAllTextAsync(file1Path, "Content 1");
        await File.WriteAllTextAsync(file2Path, "Content 2");

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var backupService = CreateBackupService(backupTargets);

        // First backup
        await backupService.Backup(CancellationToken.None);
        _testUploader.UploadedPaths.Clear();

        // Delete one file
        File.Delete(file1Path);

        // Act - Second backup
        var result = await backupService.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().BeEmpty("deleted files should not be uploaded");

        // Verify that both the initial upload run and the deletion-only run were committed
        _mockApiClient.Verify(x => x.StartBackupRun(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));

        _mockApiClient.Verify(x => x.CommitBackupRun(
            It.IsAny<Guid>(),
            It.IsAny<CommitBackupRunRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_MultipleTargets_ShouldBackupAllTargets()
    {
        // Arrange
        var target1Dir = Path.Combine(_testRoot, "multiTarget1");
        var target2Dir = Path.Combine(_testRoot, "multiTarget2");
        Directory.CreateDirectory(target1Dir);
        Directory.CreateDirectory(target2Dir);

        await File.WriteAllTextAsync(Path.Combine(target1Dir, "file1.txt"), "Content 1");
        await File.WriteAllTextAsync(Path.Combine(target2Dir, "file2.txt"), "Content 2");

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = target1Dir,
            ["photos"] = target2Dir
        };

        var backupService = CreateBackupService(backupTargets);

        // Act
        var result = await backupService.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().HaveCount(2);
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains("documents") && path.Contains("file1.txt"));
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains("photos") && path.Contains("file2.txt"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_WithBackupIgnoreFile_ShouldRespectExclusionPatterns()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target6");
        Directory.CreateDirectory(targetDir);

        await File.WriteAllTextAsync(Path.Combine(targetDir, "file1.txt"), "Content 1");
        await File.WriteAllTextAsync(Path.Combine(targetDir, "file2.tmp"), "Temp file");
        await File.WriteAllTextAsync(Path.Combine(targetDir, ".backupignore"), "*.tmp");

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var options = Options.Create(new BackupClientOptions
        {
            BackupTargets = backupTargets,
            MaxParallelUploads = 2,
            MaxFailurePercentage = 10,
            MaxRetryAttempts = 2,
            RetryDelayMs = 100,
            MaxRetryDelayMs = 1000
        });

        // Use unique in-memory database
        var dbPath = $"file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        var dbOptions = Options.Create(new DatabaseOptions { FilePath = dbPath });
        var fileSystemService = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var stateService = new BackupStateService(dbOptions, NullLogger<BackupStateService>.Instance);
        var scanner = new BackupScanner(fileSystemService, stateService, NullLogger<BackupScanner>.Instance);

        var backupService = new BackupService(
            NullLogger<BackupService>.Instance,
            _telemetry,
            _mockApiClient.Object,
            stateService,
            scanner,
            _testUploader,
            options);

        // Act
        var result = await backupService.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().HaveCount(2); // file1.txt and .backupignore itself
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains("file1.txt"));
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains(".backupignore"));
        _testUploader.UploadedPaths.Should().NotContain(path => path.Contains("file2.tmp"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_WithBackupIgnoreFile_ShouldRespectTargetSpecificIgnore()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target7");
        Directory.CreateDirectory(targetDir);

        await File.WriteAllTextAsync(Path.Combine(targetDir, "file1.txt"), "Content 1");
        await File.WriteAllTextAsync(Path.Combine(targetDir, "ignored.txt"), "Ignored content");
        await File.WriteAllTextAsync(Path.Combine(targetDir, ".backupignore"), "ignored.txt");

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var backupService = CreateBackupService(backupTargets);

        // Act
        var result = await backupService.Backup(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().HaveCount(2); // file1.txt and .backupignore itself
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains("file1.txt"));
        _testUploader.UploadedPaths.Should().Contain(path => path.Contains(".backupignore"));
        _testUploader.UploadedPaths.Should().NotContain(path => path.Contains("ignored.txt"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_WithLargeNumberOfFiles_ShouldHandleEfficiently()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target8");
        Directory.CreateDirectory(targetDir);

        // Create 100 small files
        for (int i = 0; i < 100; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(targetDir, $"file{i}.txt"), $"Content {i}");
        }

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var backupService = CreateBackupService(backupTargets);

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await backupService.Backup(CancellationToken.None);
        sw.Stop();

        // Assert
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().HaveCount(100);
        sw.ElapsedMilliseconds.Should().BeLessThan(10000, "backup of 100 small files should complete quickly");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_WithCancellation_ShouldHandleGracefully()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target9");
        Directory.CreateDirectory(targetDir);

        // Create many files to increase chance of catching during upload
        for (int i = 0; i < 50; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(targetDir, $"file{i}.txt"), $"Content {i}");
        }

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var backupService = CreateBackupService(backupTargets);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(10); // Cancel quickly

        // Act & Assert
        var act = async () => await backupService.Backup(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_ConsecutiveRuns_ShouldMaintainStateCorrectly()
    {
        // Arrange
        var targetDir = Path.Combine(_testRoot, "target10");
        Directory.CreateDirectory(targetDir);

        var backupTargets = new Dictionary<string, string>
        {
            ["documents"] = targetDir
        };

        var backupService = CreateBackupService(backupTargets);

        // Run 1: Create initial file
        await File.WriteAllTextAsync(Path.Combine(targetDir, "file1.txt"), "Content 1");
        await backupService.Backup(CancellationToken.None);
        _testUploader.UploadedPaths.Clear();

        // Run 2: Add new file
        await File.WriteAllTextAsync(Path.Combine(targetDir, "file2.txt"), "Content 2");
        await backupService.Backup(CancellationToken.None);
        _testUploader.UploadedPaths.Should().ContainSingle();
        _testUploader.UploadedPaths.Clear();

        // Run 3: Modify file
        await Task.Delay(10);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "file1.txt"), "Modified Content 1");
        await backupService.Backup(CancellationToken.None);
        _testUploader.UploadedPaths.Should().ContainSingle();
        _testUploader.UploadedPaths.Clear();

        // Run 4: Delete file
        File.Delete(Path.Combine(targetDir, "file2.txt"));
        var result = await backupService.Backup(CancellationToken.None);
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().BeEmpty();

        // Run 5: No changes
        result = await backupService.Backup(CancellationToken.None);
        result.Should().BeTrue();
        _testUploader.UploadedPaths.Should().BeEmpty();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }

        _telemetry?.Dispose();
    }
}
