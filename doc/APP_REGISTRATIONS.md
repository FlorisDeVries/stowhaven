# Microsoft Entra app registrations

The hosted user flow uses three runtime app registrations. Keep their identities and credentials separate.

The repository uses placeholders for tenant-specific IDs. Keep the real values in GitHub variables, deployment parameters, or local untracked configuration.

The Stowhaven rename does not change these IDs. Existing Entra objects may still have legacy display names such as `Backup Client` or `Backup API`; display names can be updated in the portal without changing identity or token behavior.

## Runtime applications

| Application | Configuration | Type | Purpose |
| --- | --- | --- | --- |
| Stowhaven Client | `AzureAd:ClientId` | Public/native | Signs a user in and requests the Gateway's `backup.access` scope |
| Stowhaven Gateway | `GATEWAY_AUTH_CLIENT_ID` | Confidential web/API | Protects the public Container App with Easy Auth and exchanges user tokens through OBO |
| Stowhaven API | `API_AUTH_CLIENT_ID` | Protected API | Validates internal API tokens and defines delegated scopes/application roles |

### Stowhaven Client

Required configuration:

- public client flow enabled;
- native redirect URI `http://localhost`;
- delegated permission to `api://<gateway-client-id>/backup.access`;
- no client secret.

The published client configuration points `BackupApiClient:ApiUrl` at the Gateway and `AuthenticationScope` at the Gateway—not directly at the API.

### Stowhaven Gateway

Required configuration:

- Application ID URI `api://<gateway-client-id>`;
- exposed delegated scope `backup.access`;
- client application authorized for that scope or consent granted through the tenant's normal policy;
- delegated permission to `api://<api-client-id>/backup.client`, with consent;
- a client secret for the OBO exchange;
- Container Apps redirect URI `https://<gateway-host>/.auth/login/aad/callback` for browser/Easy Auth flows;
- v2 access tokens (`requestedAccessTokenVersion: 2`).

The client secret is stored as the GitHub `GATEWAY_AUTH_CLIENT_SECRET` secret and becomes a Container App secret. Do not put it in appsettings or documentation.

The Gateway also has a system-assigned managed identity. For app-only fallback calls, assign that identity the API's `backup.gateway` application role with:

```powershell
./scripts/Grant-GatewayApiAppRole.ps1 `
  -ApiAppId "<api-app-id>" `
  -GatewayPrincipalId "<gateway-managed-identity-object-id>"
```

That role assignment is not created by the current Bicep template.

### Stowhaven API

Required configuration:

- Application ID URI/audience `api://<api-client-id>`;
- delegated scopes `backup.client` and `backup.admin`;
- application role `backup.gateway`, allowed for applications;
- v2 access tokens (`requestedAccessTokenVersion: 2`).

The API's global token gate accepts either delegated scope, or the `backup.gateway` application role. Device and restore services then isolate user data by the token's `tid` and `oid`/`sub` claims. Operational routes under `/api/ops/*` additionally require the delegated `backup.admin` scope; an app-only `backup.gateway` token does not satisfy that policy.

## Optional direct API clients

`scripts/New-BackupClientAppRegistration.ps1` creates a public client with delegated permissions directly on the Stowhaven API. This is useful for direct/API testing from a network that can reach the internal API, but it does not create the production Gateway-facing registration described above. Supply the target API URL explicitly when running the script.

`scripts/New-BackupApiAppRegistration.ps1` creates the API scopes and `backup.gateway` application role.

## Deployment identity

The GitHub Actions workload identity (recommended display name: `github-stowhaven-deploy`) is separate from all runtime app registrations. `.github/workflows/deploy.yml` uses it through OIDC; it should not have delegated backup scopes or be used by desktop clients. Its federated credential subjects must identify the current repository. Immutable GitHub subject identifiers are preferred so a future owner or repository rename does not silently change which workload is trusted.

Required repository values are documented in [GitHub Actions deployment](GITHUB_ACTIONS_DEPLOYMENT.md).

## User assignment

If access should be restricted to selected users or groups, enable **Assignment required** on the Gateway enterprise application and assign those users/groups there. The Gateway is the public resource requested by the desktop client.

Also review tenant consent for the Gateway's delegated `backup.client` permission on the API: the OBO exchange cannot request a downstream scope that has not been consented for the user/tenant.

## Related documentation

- [Authentication flow](AUTHENTICATION.md)
- [GitHub Actions deployment](GITHUB_ACTIONS_DEPLOYMENT.md)
- [`New-BackupApiAppRegistration.ps1`](../scripts/New-BackupApiAppRegistration.ps1)
- [`Grant-GatewayApiAppRole.ps1`](../scripts/Grant-GatewayApiAppRole.ps1)
