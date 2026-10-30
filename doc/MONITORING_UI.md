# Admin monitoring UI

Stowhaven includes a standalone **.NET 10 Blazor WebAssembly** application in
`src/services/monitoring`. It is a static frontend that calls the existing public
Gateway, which exchanges the operator's token for an internal API token. The API
and worker retain internal ingress. No additional application server, database,
Functions app, or linked Static Web Apps backend is needed.

## Features

- Overview of the latest 25 commit jobs, with counts explicitly scoped to that page.
- Paginated device registry and unique registered-user lists, scoped to the operator's tenant.
- Device investigation: registration/contact timestamps, latest run and successful run,
  latest run with errors, latest commit failure/retry details, and commit-lease state.
- Per-device run/commit histories and paginated current file-catalog inspection,
  including exact-path lookup, version IDs, checksums, and recorded encryption mode.
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

## Investigation workflow

Open **Devices** to browse registrations or filter by owner object ID, registration
status, and device GUID. **Users** lists unique owners of devices registered in the
current operator's tenant, including revoked registrations. Select an owner to see
their devices, then select a device to investigate it. Owners are identified by
their Entra object IDs; this uses the existing backup registry rather than reading
the tenant's directory profiles. Historical registrations appear immediately.

A device's **Recorded state** view distinguishes the latest run from its latest
fully successful run and latest run completed with errors. These are ordered by
run start time. It also displays the latest commit's error, attempts, retry and
dead-letter timestamps, plus the worker's recorded catalog lease and newest
coordinated run. Missing records are shown as unknown/unrecorded. An active
registration permits access; it does not imply the machine is online or its
backups are current. Last contact is the last device-registration refresh.

The **Backup history** and **Commit history** views load on selection and paginate
independently. Links into the main history pages prefill the device filter. Run
and commit detail pages link back to their device investigation page.

**File catalog** reads the current path-to-version mappings, including deleted
entries. Use an exact logical path (including its configured target name and
original casing) to inspect its version record, checksum, encryption mode, and
last recorded backup run. Encryption keys, wrapped-key metadata, SAS URLs, and
claim tokens are excluded. These views do not probe Blob Storage or rehydrate
archived data; catalog presence alone is not proof of restore readiness. A running
commit can temporarily change catalog mappings between reads.

Client installation version, local scheduler state, and local client logs are not
currently reported to the server and therefore remain local-machine checks.

The investigation routes use the same Gateway `monitoring.admin` and API
`backup.admin` authorization. They take the tenant from the validated API token
and check device ownership against that tenant before reading device state or
catalog data. All listings default to 25 records and accept at most 100 per request.
Distinct-owner paging uses the existing single-property index in Cosmos DB; no
new resources or owner-index backfill are needed.

| Investigation | Admin endpoint |
| --- | --- |
| Registered devices | `GET /api/ops/devices` |
| Registered backup users | `GET /api/ops/users` |
| Device registration | `GET /api/ops/devices/{deviceId}` |
| Recorded device state | `GET /api/ops/devices/{deviceId}/state` |
| Current catalog page | `GET /api/ops/devices/{deviceId}/files` |
| Exact-path metadata | `GET /api/ops/devices/{deviceId}/file?logicalPath=...` |

Deploy the API and static UI together through the existing workflow to enable
these views. Existing Entra scopes, operator settings, and hosting are reused.

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
