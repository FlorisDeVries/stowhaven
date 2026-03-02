using Azure.Core;

namespace FlorisDeV.BackupClient.Authentication;

/// <summary>
/// A no-op TokenCredential for development environments where authentication is disabled.
/// Returns an empty token that will be accepted by APIs with anonymous authentication enabled.
/// </summary>
public sealed class NoOpTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        // Return a dummy token (API accepts anonymous in development)
        return new AccessToken("dev-no-auth-token", DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
    }
}
