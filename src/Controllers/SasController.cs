using BackupApi.Models;
using BackupApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BackupApi.Controllers;

[ApiController]
[Route("api")]
public class SasController : ControllerBase
{
    private readonly ISasUrlService _sasUrlService;
    private readonly ILogger<SasController> _logger;

    public SasController(ISasUrlService sasUrlService, ILogger<SasController> logger)
    {
        _sasUrlService = sasUrlService;
        _logger = logger;
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
}
