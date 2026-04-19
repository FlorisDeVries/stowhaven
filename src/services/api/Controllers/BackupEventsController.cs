using Dapr;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Models.Events;
using FlorisDeV.BackupApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupApi.Controllers;

/// <summary>
/// Handles Dapr pub/sub events for backup processing.
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
    /// Handles BackupRunCommitted events from the backup-events topic.
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
    [Topic(DaprComponents.BackupEventsPubSub, "backup-events")]
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
            // Process the backup run synchronously
            // Note: Visibility timeout should be configured longer than processing time
            await backupProcessingService.ProcessBackupRunAsync(backupEvent, cancellationToken);

            LogBackupEventProcessedSuccess(logger, backupEvent.DeviceId, backupEvent.RunId);

            // Return 200 OK to Dapr - message will be acknowledged and removed from queue
            return Ok();
        }
        catch (Exception ex)
        {
            LogBackupEventProcessingFailed(logger, backupEvent.DeviceId, backupEvent.RunId, ex);
            
            // Return error to Dapr - message will NOT be acknowledged
            // For Storage Queues: Message becomes visible again after visibility timeout
            // After maxDequeueCount attempts, message moves to poison queue
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Backup processing failed",
                Detail = ex.Message,
                Instance = $"/api/backupevents/backup-run-committed"
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
