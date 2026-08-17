using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using FlorisDeV.BackupClient.Clients.BackupApi.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Clients.BackupApi;

public sealed class BackupApiAuthHandler(
    IOptionsSnapshot<BackupApiClientOptions> options,
    TokenCredential credential,
    ILogger<BackupApiAuthHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var authenticationTenant = options.Value.AuthenticationTenant;
        var authenticationScope = options.Value.AuthenticationScope;

        var tokenRequest = new TokenRequestContext([authenticationScope], tenantId: authenticationTenant);

        var token = await credential.GetTokenAsync(tokenRequest, cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue(token.TokenType, token.Token);

        LogTokenClaims(token.Token, authenticationScope);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            logger.LogWarning(
                "Stowhaven request returned 401 Unauthorized for {Method} {Uri}. WWW-Authenticate: {AuthenticateHeader}",
                request.Method,
                request.RequestUri,
                string.Join(" | ", response.Headers.WwwAuthenticate.Select(h => h.ToString())));
        }

        return response;
    }

    private void LogTokenClaims(string token, string configuredScope)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return;
        }

        try
        {
            var payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;
            logger.LogDebug(
                "Acquired access token for scope {Scope}. aud={Audience}, scp={Scopes}, tid={TenantId}, azp={AuthorizedParty}, appid={AppId}",
                configuredScope,
                GetClaim(payload, "aud"),
                GetClaim(payload, "scp"),
                GetClaim(payload, "tid"),
                GetClaim(payload, "azp"),
                GetClaim(payload, "appid"));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not decode access token claims for diagnostics");
        }
    }

    private static string? GetClaim(JsonElement payload, string claimName)
    {
        return payload.TryGetProperty(claimName, out var value) ? value.ToString() : null;
    }

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
