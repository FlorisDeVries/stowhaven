using FlorisDeV.Security.Authentication;
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
                // Default policy requires authentication
                options.FallbackPolicy = options.DefaultPolicy;

                // Example: Policy requiring specific role
                options.AddPolicy("RequireAdminRole", policy =>
                    policy.RequireRole("Admin", "Developer"));
            });
        }
    }
}
