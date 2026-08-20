using FlorisDeV.Security.Authentication;
using FlorisDeV.Security.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlorisDeV.Security;

public static class HostBuilderExtensions
{
    extension(WebApplicationBuilder builder)
    {
        public void AddCustomAuthentication()
        {
            if (builder.Environment.IsDevelopment())
            {
                if (!IsDevelopmentAnonymousAuthenticationExplicitlyAllowed())
                {
                    throw new InvalidOperationException(
                        "Development anonymous authentication is disabled. " +
                        "Set ALLOW_DEVELOPMENT_ANONYMOUS_AUTHENTICATION=true only for local development.");
                }

                // For local development: allow anonymous access
                // WARNING: This bypasses all authentication - never use in production!
                builder.Services
                    .AddAuthentication("AllowAnonymous")
                    .AddScheme<AuthenticationSchemeOptions,
                        AllowAnonymousAuthenticationHandler>(
                        "AllowAnonymous",
                        _ => { });
            }
            else
            {
                // Production: JWT Bearer authentication via Azure AD
                builder.Services
                    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options => options.ConfigureJwtBearer(builder.Configuration, builder.Environment));
            }

            // Configure authorization policies
            builder.Services.AddAuthorization(options =>
            {
                // Require authorization explicitly on protected endpoint groups.
                // Do not use a global fallback policy: Dapr sidecar discovery endpoints
                // such as /dapr/config and /dapr/subscribe must remain reachable from
                // the local sidecar without a JWT.
                options.FallbackPolicy = null;

                options.AddPolicy(BackupAuthorizationPolicies.Admin, policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.RequireAssertion(context =>
                        BackupAuthorizationPolicies.HasScope(
                            context.User,
                            BackupAuthorizationPolicies.AdminScope));
                });
            });
        }

    }

    private static bool IsDevelopmentAnonymousAuthenticationExplicitlyAllowed()
        => string.Equals(
            Environment.GetEnvironmentVariable("ALLOW_DEVELOPMENT_ANONYMOUS_AUTHENTICATION"),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
