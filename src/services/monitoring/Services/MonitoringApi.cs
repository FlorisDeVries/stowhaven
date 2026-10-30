using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlorisDeV.BackupContracts.Api.Responses;

namespace FlorisDeV.BackupMonitoring.Services;

public sealed class MonitoringApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<ListDevicesResponse> DevicesAsync(string? userId = null, string? status = null, Guid? deviceId = null,
        string? continuation = null, CancellationToken cancellationToken = default)
        => GetAsync<ListDevicesResponse>(Query("devices", ("userId", userId), ("status", status),
            ("deviceId", deviceId?.ToString()), ("continuationToken", continuation)), cancellationToken);

    public Task<ListBackupUsersResponse> UsersAsync(string? continuation = null, CancellationToken cancellationToken = default)
        => GetAsync<ListBackupUsersResponse>(Query("users", ("continuationToken", continuation)), cancellationToken);

    public Task<DeviceRegistrationResponse> DeviceAsync(Guid deviceId, CancellationToken cancellationToken)
        => GetAsync<DeviceRegistrationResponse>($"devices/{deviceId}", cancellationToken);

    public Task<DeviceStateResponse> DeviceStateAsync(Guid deviceId, CancellationToken cancellationToken)
        => GetAsync<DeviceStateResponse>($"devices/{deviceId}/state", cancellationToken);

    public Task<ListDeviceFilesResponse> DeviceFilesAsync(Guid deviceId, string? continuation, CancellationToken cancellationToken)
        => GetAsync<ListDeviceFilesResponse>(Query($"devices/{deviceId}/files", ("continuationToken", continuation)), cancellationToken);

    public Task<DeviceFileDetailsResponse> DeviceFileAsync(Guid deviceId, string logicalPath, CancellationToken cancellationToken)
        => GetAsync<DeviceFileDetailsResponse>(Query($"devices/{deviceId}/file", ("logicalPath", logicalPath)), cancellationToken);

    public Task<ListCommitJobsResponse> CommitsAsync(string? status = null, Guid? deviceId = null,
        string? since = null, string? continuation = null, CancellationToken cancellationToken = default)
        => GetAsync<ListCommitJobsResponse>(Query("commits", ("status", status), ("deviceId", deviceId?.ToString()),
            ("createdFromUtc", since), ("continuationToken", continuation)), cancellationToken);

    public Task<ListManifestsResponse> RunsAsync(string? status = null, Guid? deviceId = null,
        string? since = null, string? continuation = null, CancellationToken cancellationToken = default)
        => GetAsync<ListManifestsResponse>(Query("manifests", ("status", status), ("deviceId", deviceId?.ToString()),
            ("startedFromUtc", since), ("continuationToken", continuation)), cancellationToken);

    public Task<CommitJobDetailsResponse> CommitAsync(Guid id, CancellationToken cancellationToken)
        => GetAsync<CommitJobDetailsResponse>($"commits/{id}/details", cancellationToken);

    public Task<ListCommitFileProgressResponse> CommitFilesAsync(Guid id, string? continuation, CancellationToken cancellationToken)
        => GetAsync<ListCommitFileProgressResponse>(Query($"commits/{id}/files", ("continuationToken", continuation)), cancellationToken);

    public Task<ManifestDetailsResponse> RunAsync(Guid deviceId, Guid runId, CancellationToken cancellationToken)
        => GetAsync<ManifestDetailsResponse>($"manifests/{deviceId}/{runId}", cancellationToken);

    public Task<ManifestFilesResponse> RunFilesAsync(Guid deviceId, Guid runId, string? continuation, CancellationToken cancellationToken)
        => GetAsync<ManifestFilesResponse>(Query($"manifests/{deviceId}/{runId}/files", ("continuationToken", continuation)), cancellationToken);

    public async Task<CommitStatusResponse> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync($"api/ops/commits/{id}/retry", null, cancellationToken);
        return await ReadAsync<CommitStatusResponse>(response, cancellationToken);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("api/ops/" + path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your session could not be authorized. Sign out and sign in again.",
                HttpStatusCode.Forbidden => "Access denied. Your account needs the monitoring permission and must be in the gateway's operator allowlist.",
                HttpStatusCode.NotFound => "This record is no longer available.",
                HttpStatusCode.TooManyRequests => "The service is busy. Wait a moment before refreshing.",
                HttpStatusCode.BadRequest or HttpStatusCode.Conflict => "This request is no longer valid for the current data. Refresh before trying again.",
                _ => $"The gateway returned {(int)response.StatusCode}. It may be waking from idle; try refreshing in a moment."
            };
            throw new MonitoringApiException(response.StatusCode, message);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty response.");
    }

    private static string Query(string path, params (string Key, string? Value)[] values)
        => path + "?pageSize=25" + string.Concat(values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"&{pair.Key}={Uri.EscapeDataString(pair.Value!)}"));
}

public sealed class MonitoringApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
