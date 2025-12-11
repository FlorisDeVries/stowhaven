using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupApi.Authentication;

/// <summary>
/// Authentication handler for API Key authentication.
/// Validates the X-API-Key header against configured API keys.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private readonly IConfiguration _configuration;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check if the API key header is present
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeaderValues))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing API Key header"));
        }

        var providedApiKey = apiKeyHeaderValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providedApiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API Key header"));
        }

        // Get the expected API key from configuration
        // You can store this in Azure Key Vault, App Configuration, or environment variables
        var validApiKey = _configuration["Authentication:ApiKey"];
        
        if (string.IsNullOrWhiteSpace(validApiKey))
        {
            Logger.LogWarning("API Key is not configured in settings");
            return Task.FromResult(AuthenticateResult.Fail("API Key authentication is not configured"));
        }

        // Validate the API key
        if (!string.Equals(providedApiKey, validApiKey, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API Key"));
        }

        // Create claims for the authenticated user
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "ApiClient"),
            new Claim(ClaimTypes.Role, "Client")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
