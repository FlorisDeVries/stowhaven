# GitHub Actions deployment setup

This project deploys through GitHub Actions with Azure OIDC and a multi-phase workflow.

## Workflow phases

The workflow in `.github/workflows/deploy.yml` runs these phases:

1. **Validate Bicep**
   - Compiles Bicep and parameters.
   - Validates both `deployContainerApps=false` and `deployContainerApps=true` modes.

2. **Deploy foundation**
   - Deploys shared infrastructure only with `deployContainerApps=false`.
   - Creates/updates Storage, ACR, monitoring, Service Bus, Key Vault, Cosmos DB database/containers, and the ACR pull managed identity.
   - This phase is safe when ACR is still empty.

3. **Build and push images**
   - Builds `backup-api` from `src/services/api/Dockerfile`.
   - Builds `backup-worker` from `src/services/worker/Dockerfile`.
   - Pushes both the commit-SHA tag and `latest` to ACR.

4. **Deploy Container Apps**
   - Re-runs the Bicep template with `deployContainerApps=true` and `imageTag=<commit sha>`.
   - Creates/updates the API and worker Container Apps after images exist.

## One-time Azure setup

Use these commands from a local terminal where `az login` is already authenticated with an account that can create app registrations and assign roles.

Set variables:

```bash
SUBSCRIPTION_ID="6afebbda-6afc-4bf0-a8ee-740a9688d0eb"
TENANT_ID="cf8adfe1-bb3b-4ef0-8ba9-44dcddb8ecb9"
RESOURCE_GROUP="rg-fdev-weu-backup-prd"
GITHUB_ORG="FlorisDeVries"
GITHUB_REPO="backup-api"
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

az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role AcrPush \
  --scope "$RG_ID"
```

Why these roles:

- `Contributor`: deploys ARM/Bicep resources in the resource group.
- `Role Based Access Control Administrator`: lets the workflow create managed-identity role assignments declared in Bicep.
- `AcrPush`: lets the workflow push Docker images to ACR after the foundation phase creates it.

## GitHub repository variables

Create these repository variables under **Settings → Secrets and variables → Actions → Variables**:

| Variable | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | the `APP_ID` from the setup commands |
| `AZURE_TENANT_ID` | `cf8adfe1-bb3b-4ef0-8ba9-44dcddb8ecb9` |
| `AZURE_SUBSCRIPTION_ID` | `6afebbda-6afc-4bf0-a8ee-740a9688d0eb` |
| `AZURE_RESOURCE_GROUP` | `rg-fdev-weu-backup-prd` |

No Azure client secret is required. Authentication uses OIDC.

## GitHub environment

Create a GitHub environment named `production` under **Settings → Environments**.

Recommended settings:

- Require manual approval for deployments.
- Restrict deployment branches to `main`.

The workflow uses this environment on the foundation deployment job as the production approval gate. The image build and final Container Apps deployment depend on that approved foundation job, so one approval unlocks the complete multi-phase deployment.

## Running deployment

After the variables and environment are configured:

1. Push the workflow and Bicep changes to `main`.
2. Open **Actions → Deploy Backup API**.
3. Run the workflow manually, or let it run from the `main` push.
4. Approve the `production` environment when prompted.

The workflow deploys foundation first, pushes images, then deploys Container Apps with the commit SHA as the image tag.

## Local equivalent

The same phases can be run locally:

```bash
az deployment group create \
  --resource-group rg-fdev-weu-backup-prd \
  --template-file deploy/bicep/main.bicep \
  --parameters deploy/bicep/main.bicepparam \
  --parameters deployContainerApps=false

az acr login --name acrfdevweuprd

docker build -f src/services/api/Dockerfile -t acrfdevweuprd.azurecr.io/backup-api:local .
docker build -f src/services/worker/Dockerfile -t acrfdevweuprd.azurecr.io/backup-worker:local .
docker push acrfdevweuprd.azurecr.io/backup-api:local
docker push acrfdevweuprd.azurecr.io/backup-worker:local

az deployment group create \
  --resource-group rg-fdev-weu-backup-prd \
  --template-file deploy/bicep/main.bicep \
  --parameters deploy/bicep/main.bicepparam \
  --parameters imageTag=local deployContainerApps=true
```
