using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace FlorisDeV.BackupClient.Authentication;

/// <summary>
/// TokenCredential implementation using MSAL for interactive user authentication.
/// Supports distributed Windows clients with token caching and automatic refresh.
/// </summary>
public sealed class MsalTokenCredential : TokenCredential
{
    private readonly IPublicClientApplication _app;
    private readonly string[] _scopes;

    private MsalTokenCredential(IPublicClientApplication app, string[] scopes)
    {
        _app = app;
        _scopes = scopes;
    }

    /// <summary>
    /// Creates an MSAL-based TokenCredential for distributed Windows clients.
    /// </summary>
    /// <param name="clientId">Client application ID (registered as Public Client in Entra ID)</param>
    /// <param name="tenantId">Azure AD tenant ID</param>
    /// <param name="scopes">Scopes to request (e.g., "api://xxx/backup.admin")</param>
    /// <param name="authority">Authority URL (default: login.microsoftonline.com)</param>
    /// <returns>Configured TokenCredential</returns>
    public static async Task<MsalTokenCredential> CreateAsync(
        string clientId,
        string tenantId,
        string[] scopes,
        string authority = "https://login.microsoftonline.com")
    {
        var authorityUri = $"{authority.TrimEnd('/')}/{tenantId}";

        // Build the public client application
        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(authorityUri)
            .WithDefaultRedirectUri() // Uses http://localhost for desktop apps
            .Build();

        // Configure cross-platform token cache
        // This persists tokens securely on Windows (DPAPI), macOS (Keychain), Linux (libsecret)
        var storageProperties = new StorageCreationPropertiesBuilder(
                "backup-client.cache",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlorisDeV.BackupClient"))
            .WithMacKeyChain(
                serviceName: "FlorisDeV.BackupClient",
                accountName: "MSALCache")
            .WithLinuxKeyring(
                schemaName: "com.florisdev.backupclient.tokencache",
                collection: "default",
                secretLabel: "MSAL token cache",
                attribute1: new KeyValuePair<string, string>("Version", "1"),
                attribute2: new KeyValuePair<string, string>("ProductGroup", "BackupClient"))
            .Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
        cacheHelper.RegisterCache(app.UserTokenCache);

        return new MsalTokenCredential(app, scopes);
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        // Use scopes from requestContext if provided, otherwise fall back to constructor scopes
        var scopes = requestContext.Scopes?.Any() == true
            ? requestContext.Scopes.ToArray()
            : _scopes;

        try
        {
            // 1. Try silent authentication first (from cache)
            var accounts = await _app.GetAccountsAsync();
            var firstAccount = accounts.FirstOrDefault();

            if (firstAccount != null)
            {
                try
                {
                    var result = await _app
                        .AcquireTokenSilent(scopes, firstAccount)
                        .ExecuteAsync(cancellationToken);

                    return new AccessToken(result.AccessToken, result.ExpiresOn);
                }
                catch (MsalUiRequiredException)
                {
                    // Silent acquisition failed, fall through to interactive
                }
            }

            // 2. Interactive authentication required
            var interactiveResult = await _app
                .AcquireTokenInteractive(scopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cancellationToken);

            return new AccessToken(interactiveResult.AccessToken, interactiveResult.ExpiresOn);
        }
        catch (MsalException ex)
        {
            throw new AuthenticationFailedException(
                $"Failed to acquire token via MSAL: {ex.Message}",
                ex);
        }
    }

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        // MSAL is async-only, so we need to block here
        return GetTokenAsync(requestContext, cancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }
}
