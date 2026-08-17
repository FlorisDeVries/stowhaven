# Deploying Stowhaven with GitHub Actions

This project deploys through GitHub Actions with Azure OIDC and a multi-phase workflow.

## Workflow phases

The workflow in `.github/workflows/deploy.yml` runs these phases:

1. **Build and test**
   - Restores, builds, and tests `FlorisDeV.BackupApi.sln`.
   - Builds `src/services/gateway/Gateway.csproj` separately because the Gateway is not in the solution.

2. **Validate Bicep**
   - Compiles Bicep and parameters.
   - Validates both `deployContainerApps=false` and `deployContainerApps=true` modes.

3. **Deploy foundation**
   - Deploys shared infrastructure only with `deployContainerApps=false`.
   - Creates or updates Blob/Queue Storage, monitoring, Key Vault, and the database/containers in the referenced Cosmos DB account.
   - Container images do not need to exist yet.

4. **Build and push images**
   - Builds `backup-api` from `src/services/api/Dockerfile`.
   - Builds `backup-worker` from `src/services/worker/Dockerfile`.
   - Builds `gateway` from `src/services/gateway/Dockerfile`.
   - Pushes the commit-SHA tag and `latest` for all three images to GitHub Container Registry (GHCR).

5. **Deploy Container Apps**
   - Re-runs the Bicep template with `deployContainerApps=true` and `imageTag=<commit sha>`.
   - Creates or updates the internal API and worker Container Apps and the public Gateway after the images exist.

## One-time Azure setup

Use these commands from a local terminal where `az login` is already authenticated with an account that can create app registrations and assign roles.

Set variables:

```bash
SUBSCRIPTION_ID="6afebbda-6afc-4bf0-a8ee-740a9688d0eb"
TENANT_ID="cf8adfe1-bb3b-4ef0-8ba9-44dcddb8ecb9"
RESOURCE_GROUP="rg-fdev-weu-backup-prd"
GITHUB_ORG="FlorisDeVries"
GITHUB_REPO="stowhaven"
APP_NAME="github-${GITHUB_REPO}-deploy"
```

Create the Microsoft Entra application and service principal:

```bash
APP_ID=$(az ad app create \
  --display-name "$APP_NAME" \
  --query appId \
  --output tsv)

SP_OBJECT_ID=$(az ad sp create \
  --id "$APP_ID" \
  --query id \
  --output tsv)
```

Create GitHub federated credentials for `main` and the `production` environment:

```bash
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{\"name\":\"main-branch\",\"issuer\":\"https://token.actions.githubusercontent.com\",\"subject\":\"repo:${GITHUB_ORG}/${GITHUB_REPO}:ref:refs/heads/main\",\"audiences\":[\"api://AzureADTokenExchange\"]}"

az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{\"name\":\"production-environment\",\"issuer\":\"https://token.actions.githubusercontent.com\",\"subject\":\"repo:${GITHUB_ORG}/${GITHUB_REPO}:environment:production\",\"audiences\":[\"api://AzureADTokenExchange\"]}"
```

Register the Azure resource providers used by the Bicep template. This is a one-time subscription setup step and must be run by an identity with subscription-level provider registration permissions:

```bash
for namespace in \
  Microsoft.App \
  Microsoft.DocumentDB \
  Microsoft.Insights \
  Microsoft.KeyVault \
  Microsoft.ManagedIdentity \
  Microsoft.OperationalInsights \
  Microsoft.Storage; do
  az provider register --namespace "$namespace" --wait
done

az provider list \
  --query "[?namespace=='Microsoft.App' || namespace=='Microsoft.DocumentDB' || namespace=='Microsoft.Insights' || namespace=='Microsoft.KeyVault' || namespace=='Microsoft.ManagedIdentity' || namespace=='Microsoft.OperationalInsights' || namespace=='Microsoft.Storage'].{namespace:namespace,state:registrationState}" \
  --output table
```

Assign Azure roles at resource-group scope:

```bash
RG_ID=$(az group show \
  --name "$RESOURCE_GROUP" \
  --query id \
  --output tsv)

az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role Contributor \
  --scope "$RG_ID"

az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" \
  --scope "$RG_ID"

```

Why these roles:

- `Contributor`: deploys ARM/Bicep resources in the resource group.
- `Role Based Access Control Administrator`: lets the workflow create managed-identity role assignments declared in Bicep.

Images are pushed to GHCR with the workflow's `packages: write` permission, so the Azure deployment identity does not need an image-registry push role.

## GitHub repository variables or secrets

Create these as **repository-level** variables under **Settings → Secrets and variables → Actions → Variables**.


| Variable | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | the `APP_ID` from the setup commands |
| `AZURE_TENANT_ID` | `cf8adfe1-bb3b-4ef0-8ba9-44dcddb8ecb9` |
| `AZURE_SUBSCRIPTION_ID` | `6afebbda-6afc-4bf0-a8ee-740a9688d0eb` |
| `AZURE_RESOURCE_GROUP` | `rg-fdev-weu-backup-prd` |
| `GATEWAY_AUTH_CLIENT_ID` | application ID of the Gateway app registration used by Easy Auth and OBO |
| `API_AUTH_CLIENT_ID` | application ID of the protected Stowhaven API |
| `API_AUTH_AUDIENCE` | API audience, normally `api://<API_AUTH_CLIENT_ID>` |

Create these under **Actions → Secrets**:

| Secret | Value |
| --- | --- |
| `GHCR_PULL_TOKEN` | token with `read:packages`, used by Container Apps to pull private GHCR images |
| `GATEWAY_AUTH_CLIENT_SECRET` | client secret for the Gateway's OBO exchange |

No Azure deployment client secret is required. GitHub-to-Azure authentication uses OIDC. See [Authentication](AUTHENTICATION.md) for the API, Gateway, and public-client registrations.

## GitHub environment

Create a GitHub environment named `production` under **Settings → Environments**.

Recommended settings:

- Require manual approval for deployments.
- Restrict deployment branches to `main`.

The workflow uses this environment on the foundation deployment job as the production approval gate. The image build and final Container Apps deployment depend on that approved foundation job, so one approval unlocks the complete multi-phase deployment.

## Renaming the existing GitHub repository

The repository-facing configuration now expects `FlorisDeVries/stowhaven`. When renaming the existing GitHub repository:

1. Rename the repository to `stowhaven` in GitHub.
2. Update the local remote with `git remote set-url origin git@github.com:FlorisDeVries/stowhaven.git`.
3. Create or update the Azure workload identity's federated credentials so their subjects use `repo:FlorisDeVries/stowhaven:...`. Credentials tied to `repo:FlorisDeVries/backup-api:...` will no longer authorize Actions after the rename.
4. Run the deployment once to publish the images under `ghcr.io/florisdevries/stowhaven`. The service image suffixes remain `backup-api`, `backup-worker`, and `gateway` for compatibility.
5. After a successful deployment, remove obsolete federated credentials and GHCR packages only if they are no longer referenced.

GitHub redirects old repository URLs, but the OIDC subject and GHCR package path do not follow that redirect automatically.

## Running deployment

After the variables and environment are configured:

1. Push the workflow and Bicep changes to `main`.
2. Open **Actions → Deploy Stowhaven**.
3. Run the workflow manually, or let it run from the `main` push.
4. Approve the `production` environment when prompted.

The workflow deploys foundation first, pushes all three images, then deploys Container Apps with the commit SHA as the image tag.

## Local equivalent

The same phases can be run locally:

```bash
az deployment group create \
  --resource-group rg-fdev-weu-backup-prd \
  --template-file deploy/bicep/main.bicep \
  --parameters deploy/bicep/main.bicepparam \
  --parameters deployContainerApps=false

GHCR_USERNAME="FlorisDeVries"
GHCR_TOKEN="<token-with-write-and-read-packages>"
IMAGE_REGISTRY="ghcr.io/florisdevries/stowhaven"

echo "$GHCR_TOKEN" | docker login ghcr.io --username "$GHCR_USERNAME" --password-stdin

docker build -f src/services/api/Dockerfile -t "$IMAGE_REGISTRY/backup-api:local" .
docker build -f src/services/worker/Dockerfile -t "$IMAGE_REGISTRY/backup-worker:local" .
docker build -f src/services/gateway/Dockerfile -t "$IMAGE_REGISTRY/gateway:local" .
docker push "$IMAGE_REGISTRY/backup-api:local"
docker push "$IMAGE_REGISTRY/backup-worker:local"
docker push "$IMAGE_REGISTRY/gateway:local"

az deployment group create \
  --resource-group rg-fdev-weu-backup-prd \
  --template-file deploy/bicep/main.bicep \
  --parameters deploy/bicep/main.bicepparam \
  --parameters imageTag=local deployContainerApps=true \
  --parameters ghcrPullUsername="$GHCR_USERNAME" ghcrPullToken="$GHCR_TOKEN" \
  --parameters gatewayAuthClientId="$GATEWAY_AUTH_CLIENT_ID" gatewayAuthClientSecret="$GATEWAY_AUTH_CLIENT_SECRET" \
  --parameters apiAuthClientId="$API_AUTH_CLIENT_ID" apiAuthAudience="${API_AUTH_AUDIENCE:-api://$API_AUTH_CLIENT_ID}"
```

Set `GATEWAY_AUTH_CLIENT_ID`, `GATEWAY_AUTH_CLIENT_SECRET`, and `API_AUTH_CLIENT_ID` in the shell before the final deployment. `API_AUTH_AUDIENCE` is optional when the audience is `api://<API_AUTH_CLIENT_ID>`. Leaving the Gateway values empty disables Easy Auth and OBO and should only be done deliberately for a non-production environment.
