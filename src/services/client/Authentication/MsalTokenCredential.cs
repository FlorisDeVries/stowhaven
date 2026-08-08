using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace FlorisDeV.BackupClient.Authentication;

/// <summary>
/// TokenCredential implementation using MSAL for interactive user authentication.
/// Supports distributed desktop clients with token caching and automatic refresh.
/// </summary>
public sealed class MsalTokenCredential : TokenCredential
{
    private const string ReauthenticationRequiredMessage =
        "Authentication requires user interaction, but this operation runs in silent-only mode. " +
        "Run 'backup-client login' interactively, then retry the operation.";

    private readonly IMsalTokenClient _client;
    private readonly string[] _scopes;
    private readonly bool _allowInteractiveAuthentication;

    internal MsalTokenCredential(
        IMsalTokenClient client,
        string[] scopes,
        bool allowInteractiveAuthentication)
    {
        _client = client;
        _scopes = scopes;
        _allowInteractiveAuthentication = allowInteractiveAuthentication;
    }

    /// <summary>
    /// Creates an MSAL-based TokenCredential for distributed desktop clients.
    /// </summary>
    /// <param name="clientId">Client application ID (registered as Public Client in Entra ID)</param>
    /// <param name="tenantId">Azure AD tenant ID</param>
    /// <param name="scopes">Scopes to request (e.g., "api://xxx/backup.admin")</param>
    /// <param name="allowInteractiveAuthentication">Whether MSAL may open an interactive sign-in when silent acquisition fails</param>
    /// <param name="authority">Authority URL (default: login.microsoftonline.com)</param>
    /// <returns>Configured TokenCredential</returns>
    public static async Task<MsalTokenCredential> CreateAsync(
        string clientId,
        string tenantId,
        string[] scopes,
        bool allowInteractiveAuthentication,
        string authority = "https://login.microsoftonline.com")
    {
        var authorityUri = $"{authority.TrimEnd('/')}/{tenantId}";

        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(authorityUri)
            .WithDefaultRedirectUri() // Uses http://localhost for desktop apps
            .Build();

        // This persists tokens securely on Windows (DPAPI), macOS (Keychain), Linux (libsecret)
        var storageProperties = new StorageCreationPropertiesBuilder(
                "backup-client.cache",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "backup-client"))
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

        return new MsalTokenCredential(
            new MsalTokenClient(app),
            scopes,
            allowInteractiveAuthentication);
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        // Use scopes from requestContext if provided, otherwise fall back to constructor scopes
        var scopes = requestContext.Scopes.Length > 0
            ? requestContext.Scopes.ToArray()
            : _scopes;

        try
        {
            // 1. Try silent authentication first (from cache)
            var accounts = await _client.GetAccountsAsync();
            var firstAccount = accounts.FirstOrDefault();

            if (firstAccount != null)
            {
                try
                {
                    return await _client.AcquireTokenSilentAsync(scopes, firstAccount, cancellationToken);
                }
                catch (MsalUiRequiredException ex) when (!_allowInteractiveAuthentication)
                {
                    throw CreateReauthenticationRequiredException(ex);
                }
                catch (MsalUiRequiredException)
                {
                    // Explicit setup/login commands may fall through to interactive authentication.
                }
            }

            if (!_allowInteractiveAuthentication)
            {
                throw CreateReauthenticationRequiredException();
            }

            // 2. Interactive authentication required
            return await _client.AcquireTokenInteractiveAsync(scopes, cancellationToken);
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

    private static AuthenticationFailedException CreateReauthenticationRequiredException(Exception? innerException = null)
        => innerException is null
            ? new AuthenticationFailedException(ReauthenticationRequiredMessage)
            : new AuthenticationFailedException(ReauthenticationRequiredMessage, innerException);
}

internal interface IMsalTokenClient
{
    Task<IReadOnlyList<IAccount>> GetAccountsAsync();

    Task<AccessToken> AcquireTokenSilentAsync(
        string[] scopes,
        IAccount account,
        CancellationToken cancellationToken);

    Task<AccessToken> AcquireTokenInteractiveAsync(
        string[] scopes,
        CancellationToken cancellationToken);
}

internal sealed class MsalTokenClient(IPublicClientApplication app) : IMsalTokenClient
{
    public async Task<IReadOnlyList<IAccount>> GetAccountsAsync()
        => (await app.GetAccountsAsync()).ToArray();

    public async Task<AccessToken> AcquireTokenSilentAsync(
        string[] scopes,
        IAccount account,
        CancellationToken cancellationToken)
    {
        var result = await app
            .AcquireTokenSilent(scopes, account)
            .ExecuteAsync(cancellationToken);

        return new AccessToken(result.AccessToken, result.ExpiresOn);
    }

    public async Task<AccessToken> AcquireTokenInteractiveAsync(
        string[] scopes,
        CancellationToken cancellationToken)
    {
        var result = await app
            .AcquireTokenInteractive(scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken);

        return new AccessToken(result.AccessToken, result.ExpiresOn);
    }
}
