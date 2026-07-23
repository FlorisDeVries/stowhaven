using System.Diagnostics;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Clients.BackupApi.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Services;

public interface IApiWakeUpService
{
    /// <summary>
    /// Pings the API/gateway with exponential backoff until it responds, so a scaled-to-zero
    /// deployment has time to wake up before the client sends real requests.
    /// </summary>
    Task EnsureApiAwakeAsync(CancellationToken cancellationToken);
}

public partial class ApiWakeUpService(
    IBackupApiClient backupApiClient,
    IOptions<BackupApiClientOptions> options,
    ILogger<ApiWakeUpService> logger) : IApiWakeUpService
{
    private readonly ApiWakeUpOptions _options = options.Value.WakeUp;

    public async Task EnsureApiAwakeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var delay = TimeSpan.FromSeconds(_options.InitialDelaySeconds);
        var maxDelay = TimeSpan.FromSeconds(_options.MaxDelaySeconds);
        var maxWait = TimeSpan.FromSeconds(_options.MaxWaitSeconds);
        var attempt = 0;

        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await backupApiClient.Ping(cancellationToken);
                response.EnsureSuccessStatusCode();

                if (attempt > 1)
                {
                    LogApiAwake(attempt, stopwatch.Elapsed.TotalSeconds);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (stopwatch.Elapsed + delay >= maxWait)
                {
                    throw new TimeoutException(
                        $"Backup API did not respond within {maxWait.TotalSeconds:N0}s while waking up.", ex);
                }

                LogApiNotReady(ex, attempt, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
            }
        }
    }

    [LoggerMessage(LogLevel.Information, "Backup API is awake and reachable after {Attempts} attempt(s), {ElapsedSeconds:N0}s")]
    partial void LogApiAwake(int attempts, double elapsedSeconds);

    [LoggerMessage(LogLevel.Warning, "Backup API not ready yet (attempt {Attempt}); it may be waking up from a scale-to-zero state. Retrying in {DelaySeconds:N0}s...")]
    partial void LogApiNotReady(Exception exception, int attempt, double delaySeconds);
}
