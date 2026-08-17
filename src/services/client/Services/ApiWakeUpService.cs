using System.Diagnostics;
using System.Net;
using Azure.Identity;
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

public sealed partial class ApiWakeUpService(
    IHttpClientFactory httpClientFactory,
    IOptions<BackupApiClientOptions> options,
    ILogger<ApiWakeUpService> logger) : IApiWakeUpService, IDisposable
{
    public const string HttpClientName = "BackupApiWakeUp";
    public const string HealthPath = "/api/health/alive";

    private readonly ApiWakeUpOptions _options = options.Value.WakeUp;
    private readonly SemaphoreSlim _wakeLock = new(1, 1);
    private long _awakeUntilUtcTicks;

    public async Task EnsureApiAwakeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || IsRecentlyConfirmedAwake())
        {
            return;
        }

        await _wakeLock.WaitAsync(cancellationToken);
        try
        {
            // Concurrent API calls share one wake-up probe. By the time this caller acquires the
            // lock, another caller may already have brought the service online.
            if (IsRecentlyConfirmedAwake())
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var delay = TimeSpan.FromSeconds(_options.InitialDelaySeconds);
            var maxDelay = TimeSpan.FromSeconds(_options.MaxDelaySeconds);
            var maxWait = TimeSpan.FromSeconds(_options.MaxWaitSeconds);
            var probeTimeout = TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds);
            var attempt = 0;
            Exception? lastException = null;
            using var client = httpClientFactory.CreateClient(HttpClientName);

            while (true)
            {
                attempt++;
                cancellationToken.ThrowIfCancellationRequested();

                var remaining = maxWait - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw CreateTimeoutException(maxWait, lastException);
                }

                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(probeTimeout < remaining ? probeTimeout : remaining);

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, HealthPath);
                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        probeCts.Token);

                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        throw CreateAuthenticationException(response.StatusCode);
                    }

                    response.EnsureSuccessStatusCode();

                    var freshUntil = DateTimeOffset.UtcNow
                        .AddSeconds(_options.RecheckIntervalSeconds)
                        .UtcTicks;
                    Interlocked.Exchange(ref _awakeUntilUtcTicks, freshUntil);

                    if (attempt > 1)
                    {
                        LogApiAwake(attempt, stopwatch.Elapsed.TotalSeconds);
                    }

                    return;
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastException = new TimeoutException(
                        $"Stowhaven wake-up probe exceeded {probeTimeout.TotalSeconds:N0}s.", ex);
                }
                catch (HttpRequestException ex)
                    when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // Authentication failures do not improve while waiting for a cold start.
                    throw;
                }
                catch (AuthenticationFailedException)
                {
                    // A missing, expired, or revoked cached credential requires an explicit login;
                    // retrying the wake-up probe would only delay that actionable error.
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                }

                if (stopwatch.Elapsed + delay >= maxWait)
                {
                    throw CreateTimeoutException(maxWait, lastException);
                }

                LogApiNotReady(lastException!, attempt, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
            }
        }
        finally
        {
            _wakeLock.Release();
        }
    }

    private bool IsRecentlyConfirmedAwake()
        => DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _awakeUntilUtcTicks);

    private static TimeoutException CreateTimeoutException(TimeSpan maxWait, Exception? innerException)
        => new(
            $"Stowhaven did not respond within {maxWait.TotalSeconds:N0}s while waking up.",
            innerException);

    private static HttpRequestException CreateAuthenticationException(HttpStatusCode statusCode)
        => new(
            $"The authenticated Stowhaven wake-up probe was rejected with {(int)statusCode} ({statusCode}). " +
            "Run 'backup-client login' interactively, then retry the operation.",
            inner: null,
            statusCode);

    public void Dispose() => _wakeLock.Dispose();

    [LoggerMessage(LogLevel.Information, "Stowhaven is awake and reachable after {Attempts} attempt(s), {ElapsedSeconds:N0}s")]
    partial void LogApiAwake(int attempts, double elapsedSeconds);

    [LoggerMessage(LogLevel.Warning, "Stowhaven is not ready yet (attempt {Attempt}); it may be waking up from a scale-to-zero state. Retrying in {DelaySeconds:N0}s...")]
    partial void LogApiNotReady(Exception exception, int attempt, double delaySeconds);
}
