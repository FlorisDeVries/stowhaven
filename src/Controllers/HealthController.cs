using BackupApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace BackupApi.Controllers;

[ApiController]
[Route("api")]
public class HealthController : ControllerBase
{
    private readonly ILogger<HealthController> _logger;

    public HealthController(ILogger<HealthController> logger)
    {
        _logger = logger;
    }

    [HttpGet("health")]
    public ActionResult<HealthStatus> GetHealth()
    {
        try
        {
            var healthStatus = new HealthStatus
            {
                Status = "Healthy",
                Timestamp = DateTimeOffset.UtcNow,
                Version = "1.0.0",
                Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"
            };

            _logger.LogInformation("Health check performed - Status: {Status}", healthStatus.Status);
            
            return Ok(healthStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check");
            
            return StatusCode(500, new HealthStatus
            {
                Status = "Unhealthy",
                Timestamp = DateTimeOffset.UtcNow,
                Version = "1.0.0",
                Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"
            });
        }
    }
}
