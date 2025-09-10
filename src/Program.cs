using BackupApi.Services;
using Microsoft.ApplicationInsights.Extensibility;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add Application Insights
builder.Services.AddApplicationInsightsTelemetry();

// Add custom services
builder.Services.AddScoped<ISasUrlService, SasUrlService>();

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

// Middleware for API key authentication
app.Use(async (context, next) =>
{
    // Skip authentication for health endpoint
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    // Check API key for other endpoints
    var apiKey = context.Request.Headers["X-API-Key"].FirstOrDefault();
    var expectedApiKey = Environment.GetEnvironmentVariable("API_KEY");
    
    if (string.IsNullOrEmpty(apiKey) || apiKey != expectedApiKey)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }

    await next();
});

app.UseRouting();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
