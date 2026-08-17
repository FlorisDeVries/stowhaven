# Microsoft Entra app registrations

The hosted user flow uses three runtime app registrations. Keep their identities and credentials separate.

The IDs below are the values currently committed for this deployment. Replace them in configuration when creating another tenant or environment.

The Stowhaven rename does not change these IDs. Existing Entra objects may still have legacy display names such as `Backup Client` or `Backup API`; display names can be updated in the portal without changing identity or token behavior.

## Runtime applications

| Application | Current client ID | Type | Purpose |
| --- | --- | --- | --- |
| Stowhaven Client | `a862c3a8-8dfa-46b6-9a5a-5cea65652416` | Public/native | Signs a user in and requests the Gateway's `backup.access` scope |
| Stowhaven Gateway | `5506a872-9273-48f8-8145-43181d406355` | Confidential web/API | Protects the public Container App with Easy Auth and exchanges user tokens through OBO |
| Stowhaven API | `906eb0e3-e351-47c0-a68a-690207f4cccb` | Protected API | Validates internal API tokens and defines delegated scopes/application roles |

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
./scripts/Grant-GatewayApiAppRole.ps1 -GatewayPrincipalId "<gateway-managed-identity-object-id>"
```

That role assignment is not created by the current Bicep template.

### Stowhaven API

Required configuration:

- Application ID URI/audience `api://<api-client-id>`;
- delegated scopes `backup.client` and `backup.admin`;
- application role `backup.gateway`, allowed for applications;
- v2 access tokens (`requestedAccessTokenVersion: 2`).

The API's token gate currently accepts either delegated scope, or the `backup.gateway` application role. Device and restore services then isolate user data by the token's `tid` and `oid`/`sub` claims.

`backup.admin` is defined, but no endpoint-specific authorization policy currently distinguishes it from `backup.client`. In particular, `/api/ops/*` routes are protected only by the global authentication/scope gate today. Treat `backup.admin` as reserved until an explicit admin policy is implemented.

## Optional direct API clients

`scripts/New-BackupClientAppRegistration.ps1` creates a public client with delegated permissions directly on the Stowhaven API. This is useful for direct/API testing, but it does not create the production Gateway-facing registration described above. Its default API URL is also a deployment-specific placeholder; override it when using the script.

`scripts/New-BackupApiAppRegistration.ps1` creates the API scopes and `backup.gateway` application role.

## Deployment identity

The GitHub Actions workload identity (`github-backup-api-deploy` in the current tenant) is separate from all runtime app registrations. Its legacy display name does not need to change. `.github/workflows/deploy.yml` uses it through OIDC; it should not have delegated backup scopes or be used by desktop clients. Its federated credential subjects must, however, use the current `FlorisDeVries/stowhaven` repository name.

Required repository values are documented in [GitHub Actions deployment](GITHUB_ACTIONS_DEPLOYMENT.md).

## User assignment

If access should be restricted to selected users or groups, enable **Assignment required** on the Gateway enterprise application and assign those users/groups there. The Gateway is the public resource requested by the desktop client.

Also review tenant consent for the Gateway's delegated `backup.client` permission on the API: the OBO exchange cannot request a downstream scope that has not been consented for the user/tenant.

## Related documentation

- [Authentication flow](AUTHENTICATION.md)
- [GitHub Actions deployment](GITHUB_ACTIONS_DEPLOYMENT.md)
- [`New-BackupApiAppRegistration.ps1`](../scripts/New-BackupApiAppRegistration.ps1)
- [`Grant-GatewayApiAppRole.ps1`](../scripts/Grant-GatewayApiAppRole.ps1)
