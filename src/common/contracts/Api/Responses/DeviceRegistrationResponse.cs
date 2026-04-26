using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupContracts.Api.Responses;

public sealed class DeviceRegistrationResponse
{
    public Guid DeviceId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public string? DisplayName { get; init; }
    public DeviceRegistrationStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
}