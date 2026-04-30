using FlorisDeV.BackupClient.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Services;

public sealed partial class ScheduledBackupWorker(
    IBackupService backupService,
    IOptions<BackupClientOptions> options,
    ILogger<ScheduledBackupWorker> logger) : BackgroundService
{
    private readonly BackupScheduleOptions _schedule = options.Value.Schedule;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _schedule.IntervalMinutes));
        LogScheduleStarted(logger, interval);

        if (_schedule.RunOnStartup)
        {
            await RunBackupSafelyAsync(stoppingToken).ConfigureAwait(false);
        }

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunBackupSafelyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunBackupSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            LogScheduledBackupStarted(logger);
            await backupService.Backup(stoppingToken).ConfigureAwait(false);
            LogScheduledBackupCompleted(logger);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogScheduledBackupFailed(logger, ex);
        }
    }

    [LoggerMessage(LogLevel.Information, "Scheduled backup worker started with interval {Interval}")]
    private static partial void LogScheduleStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(LogLevel.Information, "Scheduled backup run started")]
    private static partial void LogScheduledBackupStarted(ILogger logger);

    [LoggerMessage(LogLevel.Information, "Scheduled backup run completed")]
    private static partial void LogScheduledBackupCompleted(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Scheduled backup run failed")]
    private static partial void LogScheduledBackupFailed(ILogger logger, Exception exception);
}
