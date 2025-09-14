using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace FlorisDeV.Logging.ApplicationInsights;

public class ContextEnrichmentTelemetryInitializer(
    IHttpContextAccessor httpContextAccessor,
    IWebHostEnvironment environment)
    : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.User is { Identity.Name: { } name })
        {
            telemetry.Context.User.AuthenticatedUserId = name;
        }

        telemetry.Context.Cloud.RoleName = environment.ApplicationName;
    }
}