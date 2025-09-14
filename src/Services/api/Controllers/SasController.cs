using FlorisDeV.BackupApi.Models;
using FlorisDeV.BackupApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FlorisDeV.BackupApi.Controllers;

[ApiController]
[Route("api")]
public class SasController(ISasUrlService sasUrlService, ILogger<SasController> logger)
    : ControllerBase
{
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

                logger.LogWarning("Invalid request model: {Errors}", errors);

                return BadRequest(new ErrorResponse
                {
                    Error = "Invalid request",
                    Details = string.Join(", ", errors)
                });
            }

            var response = await sasUrlService.GenerateUploadSasUrlAsync(request.Path!, request.TtlMinutes);
            logger.LogInformation("Generated upload SAS URL for path: {Path}", request.Path);

            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid argument for SAS URL generation");
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating upload SAS URL");
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

                logger.LogWarning("Invalid request model: {Errors}", errors);

                return BadRequest(new ErrorResponse
                {
                    Error = "Invalid request",
                    Details = string.Join(", ", errors)
                });
            }

            var response = await sasUrlService.GenerateDownloadSasUrlAsync(request.Path!, request.TtlMinutes);
            logger.LogInformation("Generated download SAS URL for path: {Path}", request.Path);

            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid argument for SAS URL generation");
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating download SAS URL");
            return StatusCode(500, new ErrorResponse
            {
                Error = "Internal server error",
                Details = "Failed to generate SAS URL"
            });
        }
    }
}