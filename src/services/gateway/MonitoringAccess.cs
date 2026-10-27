using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Gateway;

public static class MonitoringAccess
{
    public const string Policy = "MonitoringAdmin";
    public const string Scope = "monitoring.admin";

    public static void AddMonitoringAccess(this WebApplicationBuilder builder)
    {
        // Only the explicit admin policy should run this handler. Ordinary proxy
        // traffic continues to rely on Easy Auth and must not fetch JWT metadata
        // or validate its bearer token a second time on each cold start.
        AppContext.SetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", true);
        var tenantId = builder.Configuration["Gateway:OboTenantId"];
        var clientId = builder.Configuration["Gateway:OboClientId"];
        var operators = builder.Configuration.GetSection("Gateway:AdminUserObjectIds")
            .Get<string[]>() ?? [];
        var allowLocal = builder.Environment.IsDevelopment()
            && builder.Configuration.GetValue<bool>("Gateway:AllowDevelopmentAdmin");

        builder.Services.AddAuthentication().AddJwtBearer(Policy, options =>
        {
            // Independently validate admin bearer tokens. Never authorize using a decoded,
            // unvalidated token or a browser-supplied Easy Auth principal header.
            if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(clientId))
            {
                options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidAudiences = [clientId, $"api://{clientId}"],
                    ValidIssuer = $"https://login.microsoftonline.com/{tenantId}/v2.0",
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true
                };
            }
            options.MapInboundClaims = false;
        });

        builder.Services.AddAuthorization(options => options.AddPolicy(Policy, policy =>
        {
            if (allowLocal)
            {
                // Explicit opt-in, used only by the loopback-bound Docker development stack.
                policy.RequireAssertion(_ => true);
                return;
            }

            policy.AddAuthenticationSchemes(Policy);
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context =>
                !string.IsNullOrWhiteSpace(tenantId)
                && context.User.HasClaim("tid", tenantId)
                && Guid.TryParse(context.User.FindFirst("oid")?.Value, out var userId)
                && userId != Guid.Empty
                && context.User.FindAll("scp").Any(claim =>
                    claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(Scope))
                && operators.Contains(context.User.FindFirst("oid")?.Value, StringComparer.OrdinalIgnoreCase));
        }));

        var origins = builder.Configuration.GetSection("Gateway:AllowedOrigins").Get<string[]>() ?? [];
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins).WithMethods("GET", "POST")
                    .WithHeaders("Authorization", "Content-Type");
            }
        }));
    }
}
