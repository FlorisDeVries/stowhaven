namespace FlorisDeV.BackupContracts.State;

public sealed record DeviceRegistration
{
    public required Guid DeviceId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public string? DisplayName { get; init; }
    public DeviceRegistrationStatus Status { get; init; } = DeviceRegistrationStatus.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public string? ETag { get; set; }
}

public enum DeviceRegistrationStatus
{
    Active,
    Revoked
}