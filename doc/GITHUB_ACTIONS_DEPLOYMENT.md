# Deploying Stowhaven with GitHub Actions

Stowhaven deploys through GitHub Actions using Azure OpenID Connect (OIDC). No Azure client secret is stored in GitHub.

## Workflow phases

`.github/workflows/deploy.yml` runs five phases:

1. Build and test the solution and the separately hosted Gateway project.
2. Compile and validate the Bicep foundation and full deployment.
3. Deploy shared infrastructure after approval through the `production` environment.
4. Build the API, worker, and Gateway images and push them to GHCR.
5. Deploy the Container Apps with the immutable commit SHA as their image tag.

Pull requests run only the first phase. Deployment jobs receive narrowly scoped permissions, and third-party actions are pinned to commit SHAs.

## One-time Azure setup

Run these commands after `az login` with an account allowed to create app registrations and assign roles. Replace every example value for your environment.

```bash
SUBSCRIPTION_ID="<azure-subscription-id>"
TENANT_ID="<entra-tenant-id>"
RESOURCE_GROUP="<azure-resource-group>"
GITHUB_ORG="<github-owner>"
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

If reusing an existing deployment identity, set `APP_ID` to its application/client ID and resolve its service-principal object ID:

```bash
APP_ID="<existing-deployment-app-id>"
SP_OBJECT_ID=$(az ad sp show --id "$APP_ID" --query id --output tsv)
```

### Create immutable GitHub OIDC subjects

GitHub's immutable subject format binds trust to the numeric owner and repository IDs as well as their readable names. Resolve the IDs with GitHub CLI:

```bash
GITHUB_OWNER_ID=$(gh api "users/$GITHUB_ORG" --jq .id)
GITHUB_REPOSITORY_ID=$(gh api "repos/$GITHUB_ORG/$GITHUB_REPO" --jq .id)

MAIN_SUBJECT="repo:${GITHUB_ORG}@${GITHUB_OWNER_ID}/${GITHUB_REPO}@${GITHUB_REPOSITORY_ID}:ref:refs/heads/main"
PRODUCTION_SUBJECT="repo:${GITHUB_ORG}@${GITHUB_OWNER_ID}/${GITHUB_REPO}@${GITHUB_REPOSITORY_ID}:environment:production"
```

Create one credential for branch-scoped jobs (`validate` and `deploy-apps`) and one for the environment-scoped foundation job:

```bash
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{\"name\":\"stowhaven-main-immutable\",\"issuer\":\"https://token.actions.githubusercontent.com\",\"subject\":\"${MAIN_SUBJECT}\",\"audiences\":[\"api://AzureADTokenExchange\"]}"

az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{\"name\":\"stowhaven-production-immutable\",\"issuer\":\"https://token.actions.githubusercontent.com\",\"subject\":\"${PRODUCTION_SUBJECT}\",\"audiences\":[\"api://AzureADTokenExchange\"]}"
```

When migrating from an older repository name, keep the old credentials until a complete Stowhaven deployment succeeds. Then list and remove only the obsolete entries:

```bash
az ad app federated-credential list --id "$APP_ID" --output table
az ad app federated-credential delete --id "$APP_ID" --federated-credential-id "<obsolete-credential-name>"
```

### Register providers and grant deployment roles

Register the providers used by the template:

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
```

Assign the deployment identity at resource-group scope:

```bash
RG_ID=$(az group show --name "$RESOURCE_GROUP" --query id --output tsv)

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

`Contributor` deploys the resources. `Role Based Access Control Administrator` permits the role-assignment operations declared in Bicep; scope it to the deployment resource group.

## GitHub repository configuration

Create these under **Settings → Secrets and variables → Actions → Variables**:

| Variable | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | deployment identity application/client ID (`APP_ID`) |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `AZURE_RESOURCE_GROUP` | target resource-group name |
| `AZURE_NAME_SUFFIX` | existing dashed resource suffix, for example `stowhaven-weu-prd` |
| `AZURE_NAME_SUFFIX_STR` | corresponding storage-safe suffix without dashes |
| `AZURE_COSMOS_ACCOUNT_NAME` | existing Cosmos DB account name |
| `GHCR_IMAGE_REGISTRY` | lowercase package root, for example `ghcr.io/example/stowhaven` |
| `GHCR_PULL_USERNAME` | GitHub username that owns the package-read token |
| `GATEWAY_AUTH_CLIENT_ID` | Gateway application/client ID used by Easy Auth and OBO |
| `API_AUTH_CLIENT_ID` | protected API application/client ID |
| `API_AUTH_AUDIENCE` | normally `api://<API_AUTH_CLIENT_ID>` |

Create these under **Actions → Secrets**:

| Secret | Value |
| --- | --- |
| `GHCR_PULL_TOKEN` | fine-grained or classic token able to read the private GHCR packages |
| `GATEWAY_AUTH_CLIENT_SECRET` | Gateway confidential-client secret used for OBO |

The Azure IDs are identifiers, not credentials, so variables are preferred. Legacy secrets with the same Azure names can remain during migration because the workflow falls back to them when a variable is absent.

## GitHub environment

Create a `production` environment under **Settings → Environments**. Restrict it to `main` and, where your GitHub plan supports it, require a deployment reviewer.

The environment protects the foundation job. The image build and final application deployment depend on that approved job, so a single approval gates the full deployment.

## Repository and package rename

After a repository rename:

1. Point local clones at the new URL: `git remote set-url origin git@github.com:<owner>/stowhaven.git`.
2. Create new immutable OIDC credentials using the current owner/repository names and numeric IDs.
3. Update the workflow's lowercase `IMAGE_REGISTRY` path if the owner or repository changed.
4. Run a complete deployment to publish `backup-api`, `backup-worker`, and `gateway` beneath the new GHCR repository path.
5. Verify Container Apps are using the new images before removing old federated credentials or packages.

GitHub redirects ordinary repository URLs after a rename. Azure OIDC subjects and GHCR image paths are separate trust and artifact identifiers and must be migrated explicitly.

## Running deployment

Push the workflow and Bicep changes to `main`, then use **Actions → Deploy Stowhaven**, or let the `main` push trigger it. Approve the `production` environment when prompted.

Do not remove old credentials merely because validation passes. Wait for the final Container Apps deployment and a health check through the public Gateway.

## Local equivalent

Set all values explicitly; the committed Bicep parameters are safe examples, not production configuration:

```bash
RESOURCE_GROUP="<azure-resource-group>"
NAME_SUFFIX="<dashed-resource-suffix>"
NAME_SUFFIX_STR="<storage-safe-resource-suffix>"
COSMOS_ACCOUNT_NAME="<existing-cosmos-account>"
GHCR_USERNAME="<github-owner>"
GHCR_TOKEN="<token-with-read-and-write-packages>"
IMAGE_REGISTRY="ghcr.io/<lowercase-owner>/stowhaven"

az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file deploy/bicep/main.bicep \
  --parameters deploy/bicep/main.bicepparam \
  --parameters deployContainerApps=false \
  --parameters nameSuffix="$NAME_SUFFIX" nameSuffixStr="$NAME_SUFFIX_STR" cosmosAccountName="$COSMOS_ACCOUNT_NAME" containerImageRegistry="$IMAGE_REGISTRY"

echo "$GHCR_TOKEN" | docker login ghcr.io --username "$GHCR_USERNAME" --password-stdin

docker build -f src/services/api/Dockerfile -t "$IMAGE_REGISTRY/backup-api:local" .
docker build -f src/services/worker/Dockerfile -t "$IMAGE_REGISTRY/backup-worker:local" .
docker build -f src/services/gateway/Dockerfile -t "$IMAGE_REGISTRY/gateway:local" .
docker push "$IMAGE_REGISTRY/backup-api:local"
docker push "$IMAGE_REGISTRY/backup-worker:local"
docker push "$IMAGE_REGISTRY/gateway:local"

az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file deploy/bicep/main.bicep \
  --parameters deploy/bicep/main.bicepparam \
  --parameters imageTag=local deployContainerApps=true \
  --parameters nameSuffix="$NAME_SUFFIX" nameSuffixStr="$NAME_SUFFIX_STR" cosmosAccountName="$COSMOS_ACCOUNT_NAME" containerImageRegistry="$IMAGE_REGISTRY" \
  --parameters ghcrPullUsername="$GHCR_USERNAME" ghcrPullToken="$GHCR_TOKEN" \
  --parameters gatewayAuthClientId="$GATEWAY_AUTH_CLIENT_ID" gatewayAuthClientSecret="$GATEWAY_AUTH_CLIENT_SECRET" \
  --parameters apiAuthClientId="$API_AUTH_CLIENT_ID" apiAuthAudience="${API_AUTH_AUDIENCE:-api://$API_AUTH_CLIENT_ID}"
```

Set `GATEWAY_AUTH_CLIENT_ID`, `GATEWAY_AUTH_CLIENT_SECRET`, and `API_AUTH_CLIENT_ID` before the full deployment. The workflow rejects incomplete configuration, and Bicep omits Container Apps unless all Gateway and API authentication inputs are present.
