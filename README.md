# Azure Backup API

A lightweight **Python Azure Function API** that issues **temporary SAS URLs** to upload/download files to **Azure Blob Storage**, including:
- **Infrastructure as Code** with Bicep (Storage Account, Function App, Lifecycle rules, RBAC)
- **CI/CD** via GitHub Actions (OIDC login, infra deployment, code deployment)
- **Lifecycle policy**: automatically move blobs to Archive tier after 30 days of inactivity

## 📂 Project Structure

```

.
├── api/                    # Python Azure Function code
│   ├── get\_sas/            # Function: generate SAS URLs
│   │   └── **init**.py
│   ├── host.json
│   └── requirements.txt
├── infra/                  # Bicep templates for Azure resources
│   └── main.bicep
├── .github/workflows/      # GitHub Actions pipelines
│   └── deploy.yml
├── .gitignore
├── README.md

````

## 🚀 Deployment

### 1. Prerequisites
- **Azure CLI** (min. 2.57)
- **Terraform** (min. 1.5.0)
- **Python 3.11**
- **Azure Functions Core Tools v4**
- **GitHub CLI** (optional, for automated secret setup)

### 1.1. Setup GitHub OIDC Authentication
Run the provided script to automatically create the Azure AD application and GitHub secrets:

```powershell
# PowerShell
.\setup-github-secrets.ps1 "YourGitHubUsername/backup-api"

# Or with custom parameters
.\setup-github-secrets.ps1 "YourGitHubUsername/backup-api" "your-subscription-id" "CustomAppName"
```

```cmd
# Command Prompt
setup-github-secrets.cmd YourGitHubUsername/backup-api
```

This script will:
- Create an Azure AD application and service principal
- Set up federated credentials for OIDC authentication
- Assign the Contributor role to your subscription
- Optionally add the secrets to GitHub (if GitHub CLI is installed)

**Manual Setup**: If you prefer manual setup, the workflow requires these secrets:
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_TENANT_ID` 
- `AZURE_CLIENT_ID`

### 1.2. Grant User Access Administrator Role

⚠️ **IMPORTANT**: Your Service Principal needs additional permissions to create role assignments for managed identities.

After running the setup script, grant the **User Access Administrator** role:

```bash
# Replace with your actual values from the setup script output
AZURE_CLIENT_ID="your-service-principal-client-id"
AZURE_SUBSCRIPTION_ID="your-subscription-id"
RESOURCE_GROUP="rg-fdev-weu-backup-prd"

# Grant User Access Administrator role (resource group scope)
az role assignment create \
  --assignee $AZURE_CLIENT_ID \
  --role "User Access Administrator" \
  --scope "/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"
```

**Alternative: Subscription-level access** (if you plan to deploy to multiple resource groups):
```bash
az role assignment create \
  --assignee $AZURE_CLIENT_ID \
  --role "User Access Administrator" \
  --scope "/subscriptions/$AZURE_SUBSCRIPTION_ID"
```

**Verify the assignment**:
```bash
az role assignment list \
  --assignee $AZURE_CLIENT_ID \
  --output table
```

**Why this is needed**: The Service Principal needs to assign the "Storage Blob Data Contributor" role to the Function App's managed identity during deployment. Without this permission, the deployment will fail with an authorization error.

### 2. Create a Resource Group
```bash
az group create -n rg-backup-archive -l westeurope
````

### 3. Deployment via GitHub Actions

* Push to the `main` branch → triggers infra + code deployment automatically.

### 4. Local Testing

```bash
cd api
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
func start
```

## 🗝 Usage

### Request an Upload SAS URL

```bash
curl -X POST "https://<functionapp>.azurewebsites.net/api/get-sas-upload" \
  -H "x-api-key: <YOUR_API_KEY>" \
  -d '{"path":"d/Projects/file.zip","ttl_minutes":60}'
```

### Upload with AzCopy

```bash
azcopy copy "D:\Projects\file.zip" "<sas_url>" --overwrite=false
```

### Lifecycle Rule

* All blobs in `backups/` → moved to Archive tier **after 30 days** without modification.

## 📜 License

MIT License
