using FlorisDeV.BackupContracts.Events;
using FlorisDeV.BackupWorker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupWorker.Controllers;

/// <summary>
/// Handles Dapr input binding events for backup processing.
/// </summary>
[AllowAnonymous] // Dapr sidecar invokes this, not external users
[ApiController]
[Route("api/[controller]")]
public partial class BackupEventsController(
    IBackupProcessingService backupProcessingService,
    ILogger<BackupEventsController> logger
) : ControllerBase
{
    /// <summary>
    /// Handles BackupRunCommitted events from the backup-events Azure Storage Queue.
    /// Invoked by Dapr sidecar when a message is received.
    /// </summary>
    /// <remarks>
    /// This endpoint processes messages synchronously to ensure proper acknowledgment.
    /// The visibility timeout in the queue component should be set longer than the
    /// expected processing time to prevent duplicate processing.
    ///
    /// Idempotency: Processing the same event multiple times is safe because:
    /// 1. Status checks prevent reprocessing completed runs
    /// 2. Blob operations are idempotent (copy overwrites, delete is idempotent)
    /// </remarks>
    [HttpPost("backup-run-committed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> HandleBackupRunCommitted(
        [FromBody] BackupRunCommittedEvent backupEvent,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["DeviceId"] = backupEvent.DeviceId,
            ["RunId"] = backupEvent.RunId,
            ["Operation"] = "ProcessBackupRun"
        });

        LogBackupEventReceived(logger, backupEvent.DeviceId, backupEvent.RunId, backupEvent.StagingPath);

        try
        {
            await backupProcessingService.ProcessBackupRunAsync(backupEvent, cancellationToken);

            LogBackupEventProcessedSuccess(logger, backupEvent.DeviceId, backupEvent.RunId);

            return Ok();
        }
        catch (Exception ex)
        {
            LogBackupEventProcessingFailed(logger, backupEvent.DeviceId, backupEvent.RunId, ex);

            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Backup processing failed",
                Detail = ex.Message,
                Instance = "/api/backupevents/backup-run-committed"
            });
        }
    }

    #region Logging

    [LoggerMessage(LogLevel.Information,
        "Received backup run committed event for device {deviceId}, run {runId}, staging path: {stagingPath}")]
    static partial void LogBackupEventReceived(ILogger logger, Guid deviceId, Guid runId, string stagingPath);

    [LoggerMessage(LogLevel.Information,
        "Successfully processed backup run {runId} for device {deviceId}")]
    static partial void LogBackupEventProcessedSuccess(ILogger logger, Guid deviceId, Guid runId);

    [LoggerMessage(LogLevel.Error,
        "Failed to process backup run {runId} for device {deviceId}")]
    static partial void LogBackupEventProcessingFailed(ILogger logger, Guid deviceId, Guid runId, Exception ex);

    #endregion
}
