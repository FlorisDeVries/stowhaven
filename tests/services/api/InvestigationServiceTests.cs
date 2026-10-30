using System.Security.Claims;
using System.Text.Json;
using FlorisDeV.BackupApi.Controllers;
using FlorisDeV.BackupApi.Data;
using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.BackupContracts.Manifest;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FlorisDeV.BackupApi.Tests;

public sealed class InvestigationServiceTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"investigation-{Guid.NewGuid():N}.db");
    private readonly SqliteStateDocumentStore store;
    private readonly ManifestManager manifests;
    private readonly DeviceRegistryService registry;
    private readonly InvestigationService service;
    private readonly TelemetryProvider telemetry = new();
    private readonly Guid deviceId = Guid.NewGuid();

    public InvestigationServiceTests()
    {
        store = new SqliteStateDocumentStore(path, StateDocumentStoreExtensions.SerializerOptions);
        manifests = new ManifestManager(store, NullLogger<ManifestManager>.Instance, telemetry);
        registry = new DeviceRegistryService(store);
        service = new InvestigationService(store, store, registry, manifests);
    }

    [Fact]
    public async Task DevicesAreTenantScopedAndFilterByOwnerRegistrationAndId()
    {
        var active = await SeedDeviceAsync("tenant-a", "user-a");
        await SeedDeviceAsync("tenant-a", "user-a", DeviceRegistrationStatus.Revoked);
        await SeedDeviceAsync("tenant-a", "user-b");
        await SeedDeviceAsync("tenant-b", "user-a");
        var all = await service.ListDevicesAsync("tenant-a", null, null, null, 25, null, default);
        Assert.Equal(3, all.Devices.Count);
        Assert.All(all.Devices, device => Assert.Equal("tenant-a", device.TenantId));
        var filtered = await service.ListDevicesAsync("tenant-a", "user-a", DeviceRegistrationStatus.Active, active.DeviceId, 25, null, default);
        Assert.Equal(active.DeviceId, Assert.Single(filtered.Devices).DeviceId);
    }

    [Fact]
    public async Task DevicePaginationDoesNotLoadTheWholeRegistry()
    {
        for (var i = 0; i < 5; i++) await SeedDeviceAsync("tenant-a", "owner");
        var seen = new HashSet<Guid>();
        string? token = null;
        do
        {
            var result = await service.ListDevicesAsync("tenant-a", null, null, null, 2, token, default);
            Assert.InRange(result.Devices.Count, 1, 2);
            foreach (var device in result.Devices) Assert.True(seen.Add(device.DeviceId));
            token = result.NextContinuationToken;
        } while (token is not null);
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task UsersAreDistinctAcrossPagesAndIncludeHistoricalRevokedOwners()
    {
        foreach (var owner in new[] { "user-c", "user-a", "user-a", "user-b", "user-c" })
            await SeedDeviceAsync("tenant-a", owner);
        await SeedDeviceAsync("tenant-a", "user-d", DeviceRegistrationStatus.Revoked);
        await SeedDeviceAsync("tenant-b", "another-tenant-only");
        var first = await service.ListUsersAsync("tenant-a", 2, null, default);
        var second = await service.ListUsersAsync("tenant-a", 2, first.NextContinuationToken, default);
        Assert.Equal(new[] { "user-a", "user-b" }, first.Users.Select(user => user.UserId));
        Assert.Equal(new[] { "user-c", "user-d" }, second.Users.Select(user => user.UserId));
        Assert.NotNull(first.NextContinuationToken);
        Assert.Null(second.NextContinuationToken);
        Assert.All(first.Users.Concat(second.Users), user => Assert.Equal("tenant-a", user.TenantId));
    }

    [Fact]
    public async Task LatestSuccessfulRunIsFoundEvenWhenNewerRunsFailed()
    {
        await SeedDeviceAsync("tenant-a", "owner", id: deviceId);
        var start = DateTimeOffset.UtcNow.AddDays(-10);
        var succeeded = await SeedRunAsync(BackupRunStatus.Succeeded, start);
        var partial = await SeedRunAsync(BackupRunStatus.CompletedWithErrors, start.AddDays(1));
        Guid latest = default;
        for (var i = 2; i < 8; i++) latest = await SeedRunAsync(BackupRunStatus.Failed, start.AddDays(i));
        var state = await service.GetDeviceStateAsync("tenant-a", deviceId, default);
        Assert.Equal(latest, state.LatestRun!.RunId);
        Assert.Equal(succeeded, state.LatestSuccessfulRun!.RunId);
        Assert.Equal(BackupRunStatus.Succeeded, state.LatestSuccessfulRun.Status);
        Assert.Equal(partial, state.LatestRunWithErrors!.RunId);
    }

    [Fact]
    public async Task EmptyDeviceHistoryIsUnknownRatherThanAnInventedSuccessfulBackup()
    {
        await SeedDeviceAsync("tenant-a", "owner", id: deviceId);
        var state = await service.GetDeviceStateAsync("tenant-a", deviceId, default);
        Assert.Null(state.LatestRun);
        Assert.Null(state.LatestSuccessfulRun);
        Assert.Null(state.LatestRunWithErrors);
        Assert.Null(state.LatestCommit);
        Assert.Null(state.CommitState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeaseSnapshotDistinguishesUnexpiredClaimsAndDoesNotExposeClaimTokens(bool unexpired)
    {
        await SeedDeviceAsync("tenant-a", "owner", id: deviceId);
        var claim = Guid.NewGuid();
        var commitId = Guid.NewGuid();
        var latestRun = Guid.NewGuid();
        await store.UpsertAsync("deviceCommitLease", $"device:{deviceId:N}", "commit", new DeviceCommitLease
        {
            DeviceId = deviceId, CommitId = commitId, ClaimId = claim,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(unexpired ? 1 : -1), LatestRunId = latestRun
        });
        var state = await service.GetDeviceStateAsync("tenant-a", deviceId, default);
        Assert.Equal(unexpired, state.CommitState!.IsHeld);
        Assert.Equal(commitId, state.CommitState.CommitId);
        Assert.Equal(latestRun, state.CommitState.LatestCompletedRunId);
        Assert.DoesNotContain(claim.ToString(), JsonSerializer.Serialize(state));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("files")]
    [InlineData("file")]
    public async Task CrossTenantDeviceInspectionStopsBeforeReadingManifestState(string operation)
    {
        await SeedDeviceAsync("tenant-b", "owner", id: deviceId);
        var noReads = new Mock<IStateDocumentStore>(MockBehavior.Strict);
        var noManifests = new Mock<IManifestManager>(MockBehavior.Strict);
        var guarded = new InvestigationService(store, noReads.Object, registry, noManifests.Object);
        await Assert.ThrowsAsync<DeviceNotRegisteredException>(() => operation switch
        {
            "state" => (Task)guarded.GetDeviceStateAsync("tenant-a", deviceId, default),
            "files" => guarded.ListFilesAsync("tenant-a", deviceId, 25, null, default),
            _ => guarded.GetFileAsync("tenant-a", deviceId, "home/file.txt", default)
        });
        noReads.VerifyNoOtherCalls();
        noManifests.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CatalogPagesRetainDeletedMappingsAndVersionLookupOmitsEncryptionKeyMaterial()
    {
        await SeedDeviceAsync("tenant-a", "owner", id: deviceId);
        foreach (var (name, deleted) in new[] { ("home/a.txt", false), ("home/b.txt", true), ("home/c.txt", false) })
            await manifests.SaveFileEntryAsync(new FileEntry
            {
                DeviceId = deviceId, RelativePath = name, CurrentVersionId = name,
                Size = 123, LastWriteUtc = DateTimeOffset.UtcNow, LastBackupRunId = Guid.NewGuid().ToString(), IsDeleted = deleted
            });
        await manifests.SaveFileVersionAsync(new FileVersion
        {
            DeviceId = deviceId, UniqueFileId = "home/a.txt", RelativePath = "home/a.txt", Sha256 = "checksum",
            Size = 123, CreatedAt = DateTimeOffset.UtcNow, State = FileVersionState.Active,
            Encryption = new FileEncryptionMetadata
            {
                Mode = "ClientAndServer", Algorithm = "AES-GCM", KeyWrapAlgorithm = "AES-KW", Kdf = "PBKDF2",
                KdfIterations = 1, KdfSalt = "private-salt", Iv = "private-iv", WrappedKey = "private-wrapped-key", AuthenticationTag = "private-tag"
            }
        });
        var first = await service.ListFilesAsync("tenant-a", deviceId, 2, null, default);
        Assert.Equal(2, first.Files.Count);
        Assert.True(first.Files[1].IsDeleted);
        var second = await service.ListFilesAsync("tenant-a", deviceId, 2, first.NextContinuationToken, default);
        Assert.Single(second.Files);
        Assert.Null(second.NextContinuationToken);
        var detail = await service.GetFileAsync("tenant-a", deviceId, "home/a.txt", default);
        Assert.True(detail!.VersionAvailable);
        Assert.Equal("checksum", detail.Sha256);
        Assert.Equal("ClientAndServer", detail.EncryptionMode);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(detail));
        Assert.Null(await service.GetFileAsync("tenant-a", deviceId, "home/missing.txt", default));
        Assert.False((await service.GetFileAsync("tenant-a", deviceId, "home/c.txt", default))!.VersionAvailable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task InvestigationQueriesRejectUnboundedPageSizes(int pageSize)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListUsersAsync("tenant-a", pageSize, null, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListDevicesAsync("tenant-a", null, null, null, pageSize, null, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListFilesAsync("tenant-a", deviceId, pageSize, null, default));
    }

    [Fact]
    public async Task InvalidTokensAndDocumentFieldNamesAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListUsersAsync("tenant-a", 25, "not-a-token", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryDistinctValuesAsync(new DocumentQuery { Type = "device" }, "userId; DROP TABLE documents"));
    }

    [Fact]
    public void InvestigationControllerRequiresTheAdminPolicy()
    {
        var policy = Assert.Single(typeof(InvestigationController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(BackupAuthorizationPolicies.Admin, policy.Policy);
    }

    private async Task<DeviceRegistration> SeedDeviceAsync(string tenant, string user,
        DeviceRegistrationStatus status = DeviceRegistrationStatus.Active, Guid? id = null)
    {
        var device = new DeviceRegistration
        {
            DeviceId = id ?? Guid.NewGuid(), TenantId = tenant, UserId = user, DisplayName = "Test workstation",
            Status = status, CreatedAt = DateTimeOffset.UtcNow.AddDays(-5), LastSeenAt = DateTimeOffset.UtcNow
        };
        await store.UpsertAsync("device", $"device:{device.DeviceId:N}", $"{device.DeviceId:N}", device);
        return device;
    }

    private async Task<Guid> SeedRunAsync(BackupRunStatus status, DateTimeOffset startedAt)
    {
        var run = new BackupRun { DeviceId = deviceId, RunId = Guid.NewGuid(), StartedAt = startedAt, Status = status };
        await store.UpsertAsync("backupRun", $"device:{deviceId:N}", $"{run.RunId:N}", run, sortValue: startedAt.ToUnixTimeMilliseconds());
        return run.RunId;
    }

    public void Dispose()
    {
        telemetry.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
}
