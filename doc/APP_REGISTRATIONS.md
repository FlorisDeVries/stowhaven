# App registrations

This project uses several Microsoft Entra ID app registrations. They have different trust levels and should not be reused interchangeably.

## Backup API

- **Display name:** `Backup API`
- **Application/client ID:** `906eb0e3-e351-47c0-a68a-690207f4cccb`
- **Application ID URI / audience:** `api://906eb0e3-e351-47c0-a68a-690207f4cccb`
- **Purpose:** Represents the protected Backup API resource.
- **Used by:** The deployed API for JWT audience validation, and client app registrations for delegated API permissions.
- **Exposed delegated scopes:**
  - `backup.client` — normal backup/restore clients.
  - `backup.admin` — trusted administrative/operator clients.

## Backup Client

- **Display name:** `Backup Client`
- **Application/client ID:** `a862c3a8-8dfa-46b6-9a5a-5cea65652416`
- **Purpose:** Public/native app registration for regular desktop backup clients.
- **Granted API scope:** `backup.client`
- **Redirect URI:** `http://localhost`

## Backup Admin Client

- **Display name:** `Backup Admin Client`
- **Application/client ID:** `c6db3454-74c8-48ee-aa09-31101699b487`
- **Purpose:** Public/native app registration for trusted operator/admin desktop usage.
- **Granted API scopes:** `backup.client`, `backup.admin`
- **Redirect URI:** `http://localhost`

## Backup API Gateway

- **Display name:** `backup-api-gateway`
- **Application/client ID:** `5506a872-9273-48f8-8145-43181d406355`
- **Purpose:** Protects the deployed Swagger Gateway with Container Apps built-in authentication.
- **Used by:** The Gateway Container App auth configuration.
- **Redirect URI:** `https://ca-fdev-weu-prd-gateway.kinddesert-f7d01f23.westeurope.azurecontainerapps.io/.auth/login/aad/callback`

## GitHub deployment app

- **Display name:** `github-backup-api-deploy`
- **Application/client ID:** `ac236e14-a213-48a6-9872-e10ad32c339a`
- **Purpose:** GitHub Actions workload identity for deploying Azure infrastructure and Container Apps.
- **Used by:** `.github/workflows/deploy.yml` through OIDC-based Azure login.

## Assignment guidance

For user access control, use the **Enterprise Applications** blade:

1. Open the matching Enterprise Application.
2. Set **Properties > Assignment required?** to **Yes**.
3. Assign users or groups under **Users and groups**.

Use `Backup Client` for normal users and `Backup Admin Client` only for operators/admins.