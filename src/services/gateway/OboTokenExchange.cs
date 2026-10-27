using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Gateway;

public sealed record OboOptions(string ClientId, string ClientSecret, string TenantId, string[] ApiScopes);

public interface IOboTokenExchange
{
    Task<string> ExchangeAsync(OboOptions options, string userAssertion, CancellationToken cancellationToken);
}

public sealed class OboTokenExchange(IMemoryCache cache) : IOboTokenExchange
{
    public async Task<string> ExchangeAsync(OboOptions options, string userAssertion, CancellationToken cancellationToken)
    {
        // Scope is part of the key: a user's admin and client exchanges must
        // never reuse each other's delegated token.
        var key = string.Join(':', "obo", options.TenantId, options.ClientId,
            string.Join(' ', options.ApiScopes), Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(userAssertion))));
        if (cache.TryGetValue(key, out AccessToken cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached.Token;

        var credential = new OnBehalfOfCredential(options.TenantId, options.ClientId, options.ClientSecret, userAssertion);
        var result = await credential.GetTokenAsync(new TokenRequestContext(options.ApiScopes), cancellationToken);
        var expiration = result.ExpiresOn - TimeSpan.FromMinutes(5);
        if (expiration > DateTimeOffset.UtcNow)
            cache.Set(key, result, expiration);
        return result.Token;
    }
}
