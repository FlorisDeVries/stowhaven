using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlorisDeV.BackupApi.Controllers;

/// <summary>
/// Health check controller providing detailed health status information
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class HealthController(HealthCheckService healthCheckService) : ControllerBase
{
    /// <summary>
    /// Get detailed health status of the API and its dependencies
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detailed health report including all registered health checks</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetHealth(CancellationToken cancellationToken)
    {
        var report = await healthCheckService.CheckHealthAsync(cancellationToken);

        var response = new
        {
            status = report.Status.ToString(),
            totalDuration = $"{report.TotalDuration.TotalMilliseconds:F2}ms",
            timestamp = DateTimeOffset.UtcNow,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                duration = $"{entry.Value.Duration.TotalMilliseconds:F2}ms",
                exception = entry.Value.Exception?.Message,
                data = entry.Value.Data.Count > 0 ? entry.Value.Data : null,
                tags = entry.Value.Tags
            })
        };

        return report.Status == HealthStatus.Healthy
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    /// <summary>
    /// Get simple status of the API (liveness probe)
    /// </summary>
    /// <returns>OK if the API is alive</returns>
    [HttpGet("alive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetAlive()
    {
        return Ok(new { status = "Healthy", message = "API is alive" });
    }

    /// <summary>
    /// Get readiness status of the API and its dependencies (readiness probe)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>OK if the API is ready to serve requests</returns>
    [HttpGet("ready")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetReady(CancellationToken cancellationToken)
    {
        var report = await healthCheckService.CheckHealthAsync(
            check => check.Tags.Contains("ready"),
            cancellationToken);

        var response = new
        {
            status = report.Status.ToString(),
            timestamp = DateTimeOffset.UtcNow,
            dependencies = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString()
            })
        };

        return report.Status == HealthStatus.Healthy
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }
}
