using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace FlorisDeV.BackupMonitoring.Services;

// Registered only for explicitly enabled, loopback-only Development configuration.
public sealed class LocalAuthenticationStateProvider : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
        new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("name", "Local operator")], "LocalDevelopment"))));
}
