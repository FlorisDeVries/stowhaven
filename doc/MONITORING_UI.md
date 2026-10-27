# Admin monitoring UI

Stowhaven includes a standalone **.NET 10 Blazor WebAssembly** application in
`src/services/monitoring`. It is a static frontend that calls the existing public
Gateway, which exchanges the operator's token for an internal API token. The API
and worker retain internal ingress. No additional application server, database,
Functions app, or linked Static Web Apps backend is needed.

## Features

- Overview of the latest 25 commit jobs, with counts explicitly scoped to that page.
- Backup run and commit history, filtered by status, device GUID, and start date.
- Continuation-token pagination throughout, including manifest entries and file progress.
- Run details, commit progress, file errors, attempt counts, and retry/dead-letter timestamps.
- Failed-job retry with an explicit confirmation step.
- Microsoft Entra sign-in and server-enforced operator authorization.
- Responsive layouts, keyboard navigation, loading/error/empty states, and UTC timestamps.

Refresh is manual. Leaving a dashboard tab open does not poll the API or prevent
scale-to-zero. The overview reflects persisted commit state, not a guarantee of
service availability or a verified restore. It does not scan all history to
produce global totals. Infrastructure logs and alerts remain in Azure Monitor.

## Local development

Start the existing backend stack, rebuilding the Gateway:

```bash
docker compose up --build -d
dotnet run --project src/services/monitoring
```

Open `http://localhost:8300`. The launch profile uses Development and the UI's
Development settings point at `http://localhost:8200`. Docker Compose explicitly
enables `Gateway__AllowDevelopmentAdmin=true` and allows this UI origin. Its
Gateway port remains bound to the host's loopback interface.

The UI only enables its local authentication provider when all three conditions
hold: Development environment, explicit `UseDevelopmentAuthentication=true`, and
a loopback Gateway URL. The Gateway's own bypass independently requires both
Development and `Gateway:AllowDevelopmentAdmin=true`. Production ignores that
Gateway switch, and production deployment removes the UI's Development settings.

To exercise real Entra sign-in locally, set `UseDevelopmentAuthentication=false`,
configure the public settings below, add
`http://localhost:8300/authentication/login-callback` as a SPA redirect URI, and
use an authenticated Gateway that allows `http://localhost:8300` as an exact CORS
origin. The deployment template allows only the deployed static site's origin;
add a temporary explicit development origin if testing against Azure.

## Entra setup

Use the existing tenant, Gateway registration, and API registration. Add one
**public SPA registration** for the monitoring UI; it has no client secret.

1. On the **Gateway app registration**, expose delegated scope
   `monitoring.admin` under its existing `api://<gateway-client-id>` identifier URI.
   Set **Who can consent** to administrators only. Ensure it issues v2 access
   tokens (`api.requestedAccessTokenVersion = 2` in the current manifest).
2. On the **monitoring SPA registration**, select accounts in this organizational
   directory only. Add the Gateway's delegated `monitoring.admin` permission and
   grant administrator consent. Leave implicit grant disabled; the MSAL integration
   uses authorization code flow with PKCE.
3. On the **Gateway registration**, add delegated permission to the internal API's
   `backup.admin` scope and grant administrator consent. Retain the existing
   `backup.client` permission and its consent for normal backup clients.
4. Add designated operators' **user object IDs in this tenant** to
   `MONITORING_ADMIN_USER_OBJECT_IDS`. These are user object IDs, not app IDs,
   email addresses, or group IDs. An empty list denies all operators. Tenant-wide
   delegated consent alone does not grant operator access.
5. After the first deployment creates the site, add its SPA redirect URI:
   `https://<static-site-host>/authentication/login-callback`. Also add the site
   origin as a SPA redirect URI if using it for post-logout navigation.

The Gateway independently verifies the admin bearer token's signature, issuer,
audience, expiry, tenant, exact `monitoring.admin` scope, and allowlisted `oid`.
For `/api/ops/*`, it exchanges that same bearer assertion for `backup.admin` and
does not fall back to managed identity. Normal `/api/*` requests continue to use
`backup.client` by default. The OBO cache includes the requested scopes so admin
and client tokens cannot be confused. The internal API still independently
requires `backup.admin` on operations endpoints.

This also applies to scripted operators using the public Gateway's existing
`/api/ops/*` routes: they now need the Gateway monitoring scope and allowlist
membership. Existing backup clients keep their original permission.

## Azure deployment

The existing deployment workflow enables the monitoring deployment when the
repository variable `MONITORING_UI_CLIENT_ID` is populated. Add these variables:

| Variable | Value |
| --- | --- |
| `MONITORING_UI_CLIENT_ID` | Application/client ID of the new monitoring SPA |
| `MONITORING_ADMIN_USER_OBJECT_IDS` | JSON array, for example `["<operator-user-object-id>"]` |

The deployment's existing `AZURE_TENANT_ID` and `GATEWAY_AUTH_CLIENT_ID` are reused.
Register the `Microsoft.Web` resource provider once and ensure the deployment
identity can manage `Microsoft.Web/staticSites`, including the `listSecrets`
operation. Existing deployment permissions are otherwise unchanged.

The workflow:

1. Builds/tests the UI with the solution and publishes it in CI, including PRs.
2. Provisions `swa-<nameSuffix>-monitoring` on the **Free** plan during the
   foundation phase, using `deploy/bicep/modules/monitoring-ui.bicep`.
3. Passes the site's exact origin to Gateway ingress CORS and the operator IDs to
   Gateway configuration. Ingress handles unauthenticated OPTIONS preflights;
   actual operations require authentication. Credentials/cookies are not used
   for cross-origin API calls.
4. Publishes the UI with .NET 10, writes public settings from deployment outputs,
   removes Development configuration and stale precompressed settings, and uploads
   the already built static files. The short-lived workflow retrieves the site's
   deployment token via Azure OIDC and masks it; no extra persistent GitHub secret
   is required.
5. Prints the site URL and required SPA callback URI. Until Entra's callback URI
   is added, the deployed sign-in flow will reject that redirect.

The static-site control-plane region defaults to West Europe. Backend minimum
replica counts remain zero by default. The Free plan has no SLA, and backend
compute, Cosmos reads, telemetry, and network usage still follow their existing
billing. The Bicep option defaults off when the UI client ID is absent.

For direct Bicep deployments, pass `deployMonitoringUi=true` and
`monitoringAdminUserObjectIds='["<operator-object-id>"]'`. The outputs
`monitoringUiName` and `monitoringUiUrl` identify the resource to which the
published files must be uploaded. To use a custom domain, configure its Entra
redirect URI and add that exact origin to Gateway CORS as well.

## Public frontend settings

The production workflow writes `wwwroot/appsettings.json` through
`scripts/configure-monitoring-ui.py`:

```json
{
  "Monitoring": {
    "TenantId": "<tenant-id>",
    "ClientId": "<monitoring-spa-client-id>",
    "GatewayBaseUrl": "https://<gateway-host>",
    "GatewayScope": "api://<gateway-client-id>/monitoring.admin",
    "UseDevelopmentAuthentication": false
  }
}
```

These values are public identifiers. Never put a client secret, storage
credential, or API token in the static UI. The UI obtains access tokens through
MSAL and attaches them only to requests under its configured Gateway origin.
Missing or invalid configuration displays a setup screen instead of attempting
API requests.

## Verification

```bash
dotnet test tests/services/monitoring/FlorisDeV.BackupMonitoring.Tests.csproj --configuration Release
dotnet publish src/services/monitoring/FlorisDeV.BackupMonitoring.csproj --configuration Release --output /tmp/stowhaven-monitoring
az bicep build --file deploy/bicep/main.bicep
```

Integration tests exercise real JWT validation and Gateway routing: anonymous,
expired, wrong-audience, ordinary-client, and unlisted-user requests are denied;
only the configured CORS origin is allowed; production cannot enable the local
bypass; admin requests use the authorized bearer identity and `backup.admin`,
while ordinary calls still request `backup.client`. Client tests cover opaque
continuation tokens, bounded query sizes, enum serialization, retry requests,
and safe error messages.

After configuring Entra in Azure, verify sign-in and sign-out, a run detail page,
a commit detail page, and an allowlist rejection using a non-operator account.
