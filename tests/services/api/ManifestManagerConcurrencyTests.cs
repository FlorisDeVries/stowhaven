using Dapr.Client;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Constants;
using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public class ManifestManagerConcurrencyTests
{
    private readonly Mock<DaprClient> _daprClientMock;
    private readonly Mock<ILogger<ManifestManager>> _loggerMock;
    private readonly Mock<TelemetryProvider> _telemetryProviderMock;
    private readonly ManifestManager _service;

    public ManifestManagerConcurrencyTests()
    {
        _daprClientMock = new Mock<DaprClient>();
        _loggerMock = new Mock<ILogger<ManifestManager>>();
        _telemetryProviderMock  = new Mock<TelemetryProvider>();

        _service = new ManifestManager(_daprClientMock.Object, _loggerMock.Object, _telemetryProviderMock.Object);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_WithValidETag_SuccessfullyCommits()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var etag = "v1";

        var existingRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            Status = BackupRunStatus.Queued,
            StartedAt = DateTimeOffset.UtcNow
        };

        _daprClientMock
            .Setup(x => x.GetStateAndETagAsync<BackupRun>(
                DaprComponents.ManifestStateStore,
                It.IsAny<string>(),
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((existingRun, etag));

        _daprClientMock
            .Setup(x => x.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                It.IsAny<string>(),
                It.IsAny<BackupRun>(),
                etag,
                It.IsAny<StateOptions?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.CommitBackupRunAsync(deviceId, runId);

        // Assert
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.NotNull(result.CompletedAt);

        _daprClientMock.Verify(x => x.TrySaveStateAsync(
            DaprComponents.ManifestStateStore,
            It.IsAny<string>(),
            It.Is<BackupRun>(r => r.Status == BackupRunStatus.Succeeded),
            etag,
            It.IsAny<StateOptions?>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_WithETagMismatch_ThrowsConcurrentUpdateException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var etag = "v1";

        var existingRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            Status = BackupRunStatus.Queued,
            StartedAt = DateTimeOffset.UtcNow
        };

        _daprClientMock
            .Setup(x => x.GetStateAndETagAsync<BackupRun>(
                DaprComponents.ManifestStateStore,
                It.IsAny<string>(),
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((existingRun, etag));

        // Simulate ETag mismatch - another process updated the state
        _daprClientMock
            .Setup(x => x.TrySaveStateAsync(
                DaprComponents.ManifestStateStore,
                It.IsAny<string>(),
                It.IsAny<BackupRun>(),
                etag,
                It.IsAny<StateOptions?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ConcurrentUpdateException>(
            () => _service.CommitBackupRunAsync(deviceId, runId));

        Assert.Equal(deviceId, exception.DeviceId);
        Assert.Equal(runId, exception.RunId);
        Assert.Equal(etag, exception.ExpectedETag);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_AlreadyCommitted_ThrowsBackupRunAlreadyCommittedException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var etag = "v1";

        var existingRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            Status = BackupRunStatus.Succeeded, // Already committed
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        _daprClientMock
            .Setup(x => x.GetStateAndETagAsync<BackupRun>(
                DaprComponents.ManifestStateStore,
                It.IsAny<string>(),
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((existingRun, etag));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<BackupRunAlreadyCommittedException>(
            () => _service.CommitBackupRunAsync(deviceId, runId));

        Assert.Equal(deviceId, exception.DeviceId);
        Assert.Equal(runId, exception.RunId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_FailedState_ThrowsInvalidBackupRunStateException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var etag = "v1";

        var existingRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            Status = BackupRunStatus.Failed, // Failed state
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };

        _daprClientMock
            .Setup(x => x.GetStateAndETagAsync<BackupRun>(
                DaprComponents.ManifestStateStore,
                It.IsAny<string>(),
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((existingRun, etag));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidBackupRunStateException>(
            () => _service.CommitBackupRunAsync(deviceId, runId));

        Assert.Equal(deviceId, exception.DeviceId);
        Assert.Equal(runId, exception.RunId);
        Assert.Equal(BackupRunStatus.Failed, exception.CurrentStatus);
        Assert.Equal(BackupRunStatus.Queued, exception.ExpectedStatus);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CommitBackupRunAsync_RunNotFound_ThrowsBackupRunNotFoundException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        _daprClientMock
            .Setup(x => x.GetStateAndETagAsync<BackupRun>(
                DaprComponents.ManifestStateStore,
                It.IsAny<string>(),
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))!
            .ReturnsAsync(((BackupRun?)null, (string?)null));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<BackupRunNotFoundException>(
            () => _service.CommitBackupRunAsync(deviceId, runId));

        Assert.Equal(deviceId, exception.DeviceId);
        Assert.Equal(runId, exception.RunId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetBackupRunAsync_StoresETagInModel()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var etag = "v1";

        var existingRun = new BackupRun
        {
            DeviceId = deviceId,
            RunId = runId,
            Status = BackupRunStatus.Queued,
            StartedAt = DateTimeOffset.UtcNow
        };

        _daprClientMock
            .Setup(x => x.GetStateAndETagAsync<BackupRun>(
                DaprComponents.ManifestStateStore,
                It.IsAny<string>(),
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((existingRun, etag));

        // Act
        var result = await _service.GetBackupRunAsync(deviceId, runId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(etag, result.ETag);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetBackupRunAsync_NotFound_ThrowsBackupRunNotFoundException()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        _daprClientMock
            .Setup(x => x.GetStateAndETagAsync<BackupRun>(
                DaprComponents.ManifestStateStore,
                It.IsAny<string>(),
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))!
            .ReturnsAsync(((BackupRun?)null, (string?)null));

        // Act & Assert
        await Assert.ThrowsAsync<BackupRunNotFoundException>(
            () => _service.GetBackupRunAsync(deviceId, runId));
    }
}
