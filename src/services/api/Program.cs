using System.Reflection;
using FlorisDeV.BackupApi;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Filters;
using FlorisDeV.HealthChecks;
using FlorisDeV.Logging;
using FlorisDeV.Logging.ErrorHandling;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var appAssembly = Assembly.GetExecutingAssembly();
var environment = builder.Environment;

// Add services to the container
builder.AddOpenTelemetry(Telemetry.ActivitySourceName, environment.IsDevelopment());
builder.AddApplicationInsights();
builder.AddCustomLogging();
builder.AddCustomSwagger(appAssembly);
builder.AddCustomDaprClient();
builder.AddCustomCache();
builder.AddCustomRateLimitPolicies();
builder.AddStandardHealthChecks();

builder.AddCustomAuthentication();

builder.AddApplicationServices();
builder.ConfigureRouting();
builder.ConfigureWebServer();
builder.ConfigureProxyForwarding();

builder.Services
    .AddEndpointsApiExplorer()
    .AddControllers(options =>
    {
        options.Filters.Add<ProblemDetailsResultFilter>();
        options.Filters.Add<GlobalExceptionFilter>();
    })
    .AddCustomDaprIntegration();

var app = builder.Build();

app.UseForwardedHeaders();
// app.UseSecurityHeaders();
app.UseExceptionHandler();

app.UseCloudEvents();

app.UseCustomSwagger(environment.ApplicationName);

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Map controllers - RequireAuthorization applies authentication
// In development, anonymous auth handler allows all requests
app.MapControllers().RequireAuthorization();
app.MapSubscribeHandler();

app.MapStandardHealthChecks()
    .MapHealthChecks("/healthz").RequireRateLimiting(RateLimitPolicies.ExternalHealthCheckPolicy);

app.MapGet("/", () => Results.LocalRedirect("~/swagger")).ExcludeFromDescription();

try
{
    app.Logger.LogInformation("Starting {ApplicationName}...", environment.ApplicationName);
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