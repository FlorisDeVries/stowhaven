using System.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FlorisDeV.Security.Authentication;

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
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            IssuerValidator = (issuer, token, parameters) =>
            {
                // Accept any issuer from our tenant
                if (issuer.StartsWith($"{instance}{tenantId}", StringComparison.OrdinalIgnoreCase))
                    return issuer;

                throw new SecurityTokenInvalidIssuerException($"Invalid issuer: {issuer}");
            },

            ClockSkew = TimeSpan.FromMinutes(5) // Allow 5 minutes clock skew
        };

        // Configure events for logging and debugging
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(JwtBearerAuthenticationHandler));

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
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(JwtBearerAuthenticationHandler));

                var userId = context.Principal?.GetUserId();

                var scopeClaim = context.Principal?.FindFirst("scp")?.Value;
                var scopes = scopeClaim?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                if (!scopes.Contains("backup.client") && !scopes.Contains("backup-admin"))
                {
                    context.Fail("Missing required scope: backup.client");
                }

                logger.LogDebug("JWT token validated for user: {UserId}", userId);

                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(JwtBearerAuthenticationHandler));

                if (!context.Response.HasStarted)
                {
                    logger.LogInformation(
                        "JWT authentication challenge for {Path}: {Error}",
                        context.Request.Path,
                        context.ErrorDescription ?? "Unauthorized");
                }

                return Task.CompletedTask;
            },
            OnMessageReceived = _ => Task.CompletedTask
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

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user)
        => user.FindFirst("oid")?.Value
        ?? user.FindFirst("sub")?.Value
        ?? throw new SecurityException("User ID not found in token");

    public static string GetTenantId(this ClaimsPrincipal user)
        => user.FindFirst("tid")?.Value
        ?? throw new SecurityException("Tenant ID not found in token");
}