using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupApi.Authentication;

/// <summary>
/// Authentication handler that allows all requests without authentication.
/// Used for local development only.
/// WARNING: Never use this handler in production environments.
/// </summary>
public class AllowAnonymousAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private readonly ILogger<AllowAnonymousAuthenticationHandler> _logger =
        logger.CreateLogger<AllowAnonymousAuthenticationHandler>();

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        _logger.LogWarning("Allowing anonymous access. Exception made for DEVELOPMENT environment. " +
                           "If you see this message in production, stop immediately and fix your authentication configuration!");

        // Create a fake identity for anonymous access
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "Developer"),
            new Claim(ClaimTypes.NameIdentifier, "dev-user"),
            new Claim(ClaimTypes.Role, "Developer"),
            new Claim("environment", "development")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}