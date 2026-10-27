using FlorisDeV.BackupClient.Config;
using FlorisDeV.BackupClient.Models;
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
            var result = await backupService.RunAsync(stoppingToken).ConfigureAwait(false);
            await WriteResultAsync(result, stoppingToken);
            logger.Log(result.Outcome == BackupOutcome.CompletedWithErrors ? LogLevel.Warning : LogLevel.Information,
                "Scheduled backup outcome {Outcome}; run {RunId}, commit {CommitId}", result.Outcome, result.RunId, result.CommitId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogScheduledBackupFailed(logger, ex);
            await WriteResultAsync(new BackupResult { Outcome = BackupOutcome.Failed, ErrorType = ex.GetType().Name }, stoppingToken);
        }
    }

    private async Task WriteResultAsync(BackupResult result, CancellationToken token)
    {
        try { await result.WriteAsync(options.Value.ResultFilePath, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not write scheduled backup result file");
        }
    }

    [LoggerMessage(LogLevel.Information, "Scheduled backup worker started with interval {Interval}")]
    private static partial void LogScheduleStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(LogLevel.Information, "Scheduled backup run started")]
    private static partial void LogScheduledBackupStarted(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Scheduled backup run failed")]
    private static partial void LogScheduledBackupFailed(ILogger logger, Exception exception);
}
