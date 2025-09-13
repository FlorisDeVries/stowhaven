using BackupApi.Models;
using BackupApi.Services;
using Microsoft.AspNetCore.Mvc;
using Dapr;
using Dapr.Client;

namespace BackupApi.Controllers;

[ApiController]
[Route("api")]
public class SasController : ControllerBase
{
    private readonly ISasUrlService _sasUrlService;
    private readonly ILogger<SasController> _logger;
    private readonly DaprClient _daprClient;

    public SasController(ISasUrlService sasUrlService, ILogger<SasController> logger, DaprClient daprClient)
    {
        _sasUrlService = sasUrlService;
        _logger = logger;
        _daprClient = daprClient;
    }

    [HttpPost("get-sas-upload")]
    public async Task<ActionResult<SasResponse>> GetSasUpload([FromBody] SasRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage);
                
                _logger.LogWarning("Invalid request model: {Errors}", string.Join(", ", errors));
                
                return BadRequest(new ErrorResponse 
                { 
                    Error = "Invalid request", 
                    Details = string.Join(", ", errors) 
                });
            }

            var response = await _sasUrlService.GenerateUploadSasUrlAsync(request.Path!, request.TtlMinutes);
            _logger.LogInformation("Generated upload SAS URL for path: {Path}", request.Path);
            
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument for SAS URL generation");
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating upload SAS URL");
            return StatusCode(500, new ErrorResponse 
            { 
                Error = "Internal server error", 
                Details = "Failed to generate SAS URL" 
            });
        }
    }

    [HttpPost("get-sas-download")]
    public async Task<ActionResult<SasResponse>> GetSasDownload([FromBody] SasRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage);
                
                _logger.LogWarning("Invalid request model: {Errors}", string.Join(", ", errors));
                
                return BadRequest(new ErrorResponse 
                { 
                    Error = "Invalid request", 
                    Details = string.Join(", ", errors) 
                });
            }

            var response = await _sasUrlService.GenerateDownloadSasUrlAsync(request.Path!, request.TtlMinutes);
            _logger.LogInformation("Generated download SAS URL for path: {Path}", request.Path);
            
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument for SAS URL generation");
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating download SAS URL");
            return StatusCode(500, new ErrorResponse 
            { 
                Error = "Internal server error", 
                Details = "Failed to generate SAS URL" 
            });
        }
    }

    /// <summary>
    /// DAPR pub/sub event handler for backup completion events
    /// This endpoint will be called by DAPR when events are published to the backup-completed topic
    /// </summary>
    [HttpPost("backup-completed")]
    [Topic("backup-pubsub", "backup-completed")]
    public async Task<ActionResult> HandleBackupCompleted([FromBody] BackupCompletedEvent backupEvent)
    {
        try
        {
            _logger.LogInformation("Received backup completed event for path: {Path}, Success: {Success}", 
                backupEvent.Path, backupEvent.Success);

            // Store backup completion info in DAPR state store
            var stateKey = $"backup-status:{backupEvent.Path}";
            await _daprClient.SaveStateAsync("statestore", stateKey, backupEvent);

            // You could add additional logic here like:
            // - Sending notifications
            // - Updating databases
            // - Triggering cleanup processes
            // - etc.

            return Ok(new { Message = "Backup completion event processed successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing backup completed event");
            return StatusCode(500, new ErrorResponse
            {
                Error = "Failed to process backup completed event",
                Details = ex.Message
            });
        }
    }

    /// <summary>
    /// Get backup status for a specific path
    /// </summary>
    [HttpGet("backup-status/{**path}")]
    public async Task<ActionResult<BackupCompletedEvent>> GetBackupStatus(string path)
    {
        try
        {
            var stateKey = $"backup-status:{path}";
            var backupStatus = await _daprClient.GetStateAsync<BackupCompletedEvent>("statestore", stateKey);
            
            if (backupStatus == null)
            {
                return NotFound(new ErrorResponse { Error = "Backup status not found for the specified path" });
            }

            return Ok(backupStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting backup status for path: {Path}", path);
            return StatusCode(500, new ErrorResponse
            {
                Error = "Failed to get backup status",
                Details = ex.Message
            });
        }
    }
}
