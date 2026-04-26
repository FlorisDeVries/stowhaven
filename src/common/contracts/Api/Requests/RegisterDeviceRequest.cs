namespace FlorisDeV.BackupContracts.Api.Requests;

public sealed class RegisterDeviceRequest
{
    public Guid? DeviceId { get; init; }
    public string? DisplayName { get; init; }
}