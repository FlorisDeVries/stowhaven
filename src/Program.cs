using BackupApi.Services;
using Microsoft.ApplicationInsights.Extensibility;
using Dapr.Client;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers().AddDapr();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add DAPR client
builder.Services.AddDaprClient();

// Add Application Insights
builder.Services.AddApplicationInsightsTelemetry();

// Add custom services
builder.Services.AddScoped<ISasUrlService, DaprSasUrlService>();

// Add health checks
builder.Services.AddHealthChecks()
    .AddCheck<StorageHealthCheck>("storage");

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Middleware for API key authentication using DAPR secrets
app.Use(async (context, next) =>
{
    // Skip authentication for health endpoint
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    // Check API key for other endpoints using DAPR
    var apiKey = context.Request.Headers["X-API-Key"].FirstOrDefault();
    
    if (string.IsNullOrEmpty(apiKey))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized - API key required");
        return;
    }

    // In production, we'll validate against DAPR secret store
    // For now, fall back to environment variable
    var expectedApiKey = Environment.GetEnvironmentVariable("API_KEY");
    
    if (apiKey != expectedApiKey)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized - Invalid API key");
        return;
    }

    await next();
});

app.UseRouting();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
