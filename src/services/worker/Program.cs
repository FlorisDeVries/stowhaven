using System.Reflection;
using FlorisDeV.BackupApi;
using FlorisDeV.BackupApi.Filters;
using FlorisDeV.BackupApi.Telemetry;
using FlorisDeV.FeatureFlags;
using FlorisDeV.HealthChecks;
using FlorisDeV.Logging;
using FlorisDeV.Logging.ErrorHandling;
using FlorisDeV.Logging.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var appAssembly = Assembly.GetExecutingAssembly();
var environment = builder.Environment;

builder.AddOpenTelemetry(TelemetryProvider.SourceName, environment.IsDevelopment());
builder.AddApplicationInsights();
builder.AddCustomLogging();
builder.AddCustomSwagger(appAssembly);
builder.AddCustomDaprClient();
builder.AddCustomCache();
builder.AddStandardHealthChecks();
builder.AddApplicationServices();
builder.ConfigureRouting();
builder.ConfigureWebServer();
builder.ConfigureProxyForwarding();
builder.AddAzureFeatureFlags();

builder.Services
    .AddExceptionHandlers()
    .AddEndpointsApiExplorer()
    .AddControllers(options =>
    {
        options.Filters.Add<ProblemDetailsResultFilter>();
        options.Filters.Add<GlobalExceptionFilter>();
        options.Conventions.Add(new WorkerControllerAssemblyConvention(appAssembly));
    })
    .AddApplicationPart(appAssembly)
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

app.MapControllers();
app.MapSubscribeHandler();

app.MapStandardHealthChecks();
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = HealthCheckResponseWriter.WriteDetailedResponse
});

try
{
    app.Logger.LogInformation("Starting {ApplicationName}...", environment.ApplicationName);
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

internal sealed class WorkerControllerAssemblyConvention(Assembly workerAssembly) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        for (var i = application.Controllers.Count - 1; i >= 0; i--)
        {
            if (application.Controllers[i].ControllerType.Assembly != workerAssembly)
            {
                application.Controllers.RemoveAt(i);
            }
        }
    }
}
