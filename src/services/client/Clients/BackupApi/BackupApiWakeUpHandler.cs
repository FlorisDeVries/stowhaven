using FlorisDeV.BackupClient.Services;

namespace FlorisDeV.BackupClient.Clients.BackupApi;

/// <summary>
/// Automatically wakes a scaled-to-zero API before every logical Refit request. The wake-up service
/// caches recent success and coalesces concurrent probes, so clustered calls do not each issue a ping.
/// </summary>
public sealed class BackupApiWakeUpHandler(IApiWakeUpService wakeUpService) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                request.RequestUri?.AbsolutePath,
                ApiWakeUpService.HealthPath,
                StringComparison.OrdinalIgnoreCase))
        {
            await wakeUpService.EnsureApiAwakeAsync(cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
