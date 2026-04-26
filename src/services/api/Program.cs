using System.Reflection;
using FlorisDeV.BackupApi;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Filters;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.FeatureFlags;
using FlorisDeV.HealthChecks;
using FlorisDeV.Logging;
using FlorisDeV.Logging.ErrorHandling;
using FlorisDeV.Logging.Middleware;
using FlorisDeV.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var appAssembly = Assembly.GetExecutingAssembly();
var environment = builder.Environment;

// Add services to the container
builder.AddOpenTelemetry(TelemetryProvider.SourceName, environment.IsDevelopment());
builder.AddApplicationInsights();
builder.AddCustomLogging();
builder.AddCustomSwagger(appAssembly);
builder.AddCustomDaprClient();
builder.AddCustomCache();
builder.AddCustomRateLimitPolicies();
builder.AddStandardHealthChecks();

builder.AddCustomAuthentication();

builder.AddApplicationServices();
builder.AddBackupApiServices();
builder.ConfigureRouting();
builder.ConfigureWebServer();
builder.ConfigureProxyForwarding();
builder.AddAzureFeatureFlags();

builder.Services
    .AddExceptionHandlers() // Register all exception handlers
    .AddEndpointsApiExplorer()
    .AddControllers(options =>
    {
        options.Filters.Add<ProblemDetailsResultFilter>();
        options.Filters.Add<GlobalExceptionFilter>();
    })
    .AddCustomDaprIntegration();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();

app.UseCorrelationId();
app.UseLogSampling();

app.UseAzureFeatureFlags();

app.UseCloudEvents();

app.UseCustomSwagger(environment.ApplicationName);

app.UseRouting();
app.UseAuthentication();

app.UseUserContextEnrichment();

app.UseAuthorization();
app.UseRateLimiter();

// Map controllers - RequireAuthorization applies authentication
// In development, anonymous auth handler allows all requests
app.MapControllers().RequireAuthorization();
app.MapSubscribeHandler();

// Health check endpoints
app.MapStandardHealthChecks();

// Additional health check endpoint with detailed information (anonymous for monitoring)
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = HealthCheckResponseWriter.WriteDetailedResponse
}).RequireRateLimiting(RateLimitPolicies.ExternalHealthCheckPolicy);

app.MapGet("/", () => Results.LocalRedirect("~/swagger")).ExcludeFromDescription();

try
{
    app.Logger.LogInformation("Starting {ApplicationName}...", environment.ApplicationName);
    
    // Log startup configuration for troubleshooting
    app.LogStartupConfiguration();
    
    app.Run();
}
catch (Exception error)
{
    app.Logger.LogCritical(error, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
