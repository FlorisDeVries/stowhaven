using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace FlorisDeV.BackupApi.Authentication;

/// <summary>
/// Configures JWT Bearer authentication for Azure AD.
/// Handles token validation and authentication events.
/// </summary>
public static class JwtBearerAuthenticationHandler
{
    /// <summary>
    /// Configures JWT Bearer authentication with Azure AD settings.
    /// </summary>
    public static void ConfigureJwtBearer(this JwtBearerOptions options, IConfiguration configuration, IHostEnvironment environment)
    {
        var azureAd = configuration.GetSection("AzureAd");
        
        // Validate required configuration
        var instance = azureAd["Instance"] ?? throw new InvalidOperationException("AzureAd:Instance is required");
        var tenantId = azureAd["TenantId"] ?? throw new InvalidOperationException("AzureAd:TenantId is required");
        var audience = azureAd["Audience"] ?? throw new InvalidOperationException("AzureAd:Audience is required");
        
        var authority = $"{instance}{tenantId}/v2.0";
        
        options.Authority = authority;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidAudience = audience,
            ValidIssuer = authority,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(5) // Allow 5 minutes clock skew
        };

        // Configure events for logging and debugging
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILogger<Program>>();
                
                logger.LogWarning(
                    "JWT authentication failed for {Path}: {Exception}", 
                    context.Request.Path, 
                    context.Exception.Message);

                if (environment.IsDevelopment())
                {
                    // In development, provide more detailed error information
                    logger.LogDebug("Authentication failure details: {Details}", context.Exception.ToString());
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILogger<Program>>();
                
                var userId = context.Principal?.Identity?.Name 
                    ?? context.Principal?.FindFirst("preferred_username")?.Value 
                    ?? context.Principal?.FindFirst("sub")?.Value 
                    ?? "Unknown";
                
                logger.LogDebug("JWT token validated for user: {UserId}", userId);
                
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILogger<Program>>();
                
                if (!context.Response.HasStarted)
                {
                    logger.LogInformation(
                        "JWT authentication challenge for {Path}: {Error}", 
                        context.Request.Path, 
                        context.ErrorDescription ?? "Unauthorized");
                }
                
                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                // Optional: Support token from query string for specific scenarios (e.g., SignalR)
                // Uncomment if needed:
                // var accessToken = context.Request.Query["access_token"];
                // if (!string.IsNullOrEmpty(accessToken))
                // {
                //     context.Token = accessToken;
                // }
                
                return Task.CompletedTask;
            }
        };

        // Additional security options
        options.SaveToken = false; // Don't save token in AuthenticationProperties (reduces memory usage)
        options.RequireHttpsMetadata = !environment.IsDevelopment(); // Require HTTPS in production
        
        if (environment.IsDevelopment())
        {
            // In development, you might want to see more details
            options.IncludeErrorDetails = true;
        }
    }
}
