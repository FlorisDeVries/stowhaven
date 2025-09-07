# Setup GitHub Secrets for Azure OIDC Authentication
# This script creates the necessary Azure AD application and service principal
# for GitHub Actions to authenticate with Azure using OIDC (no secrets!)

param(
    [Parameter(Mandatory=$true)]
    [string]$GitHubRepo,
    
    [Parameter(Mandatory=$false)]
    [string]$SubscriptionId = "",
    
    [Parameter(Mandatory=$false)]
    [string]$AppName = "GitHubActions-BackupAPI"
)

# Colors for output
$Red = "`e[31m"
$Green = "`e[32m"
$Yellow = "`e[33m"
$Blue = "`e[34m"
$Reset = "`e[0m"

function Write-Step {
    param([string]$Message)
    Write-Host "${Blue}🔧 $Message${Reset}"
}

function Write-Success {
    param([string]$Message)
    Write-Host "${Green}✅ $Message${Reset}"
}

function Write-Warning {
    param([string]$Message)
    Write-Host "${Yellow}⚠️  $Message${Reset}"
}

function Write-Error {
    param([string]$Message)
    Write-Host "${Red}❌ $Message${Reset}"
}

# Validate GitHub repo format
if ($GitHubRepo -notmatch "^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$") {
    Write-Error "GitHub repository must be in format 'owner/repo' (e.g., 'FlorisDeVries/backup-api')"
    exit 1
}

Write-Host "${Blue}🚀 Setting up GitHub OIDC Authentication for Azure${Reset}"
Write-Host "Repository: $GitHubRepo"
Write-Host "App Name: $AppName"
Write-Host ""

# Check if Azure CLI is installed and logged in
Write-Step "Checking Azure CLI..."
try {
    $null = az account show 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Please login to Azure CLI first: az login"
        exit 1
    }
    Write-Success "Azure CLI is authenticated"
} catch {
    Write-Error "Azure CLI not found. Please install it from: https://docs.microsoft.com/en-us/cli/azure/install-azure-cli"
    exit 1
}

# Get subscription info
if ([string]::IsNullOrEmpty($SubscriptionId)) {
    $currentSub = az account show --query "{id:id, name:name}" -o json | ConvertFrom-Json
    $SubscriptionId = $currentSub.id
    Write-Host "Using current subscription: $($currentSub.name) ($SubscriptionId)"
} else {
    az account set --subscription $SubscriptionId
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to set subscription: $SubscriptionId"
        exit 1
    }
}

# Get tenant ID
$tenantId = az account show --query "tenantId" -o tsv

Write-Step "Creating Azure AD Application..."
$appId = az ad app create `
    --display-name $AppName `
    --query "appId" -o tsv

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create Azure AD application"
    exit 1
}
Write-Success "Created application: $AppName ($appId)"

Write-Step "Creating service principal..."
$principalId = az ad sp create --id $appId --query "id" -o tsv
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create service principal"
    exit 1
}
Write-Success "Created service principal: $principalId"

Write-Step "Adding Contributor role assignment..."
az role assignment create `
    --assignee $appId `
    --role "Contributor" `
    --scope "/subscriptions/$SubscriptionId"

if ($LASTEXITCODE -ne 0) {
    Write-Warning "Failed to assign Contributor role. You may need to do this manually."
} else {
    Write-Success "Assigned Contributor role to service principal"
}

Write-Step "Creating federated credential for main branch..."
$federatedCredential = @{
    name = "main-branch"
    issuer = "https://token.actions.githubusercontent.com"
    subject = "repo:$GitHubRepo`:ref:refs/heads/main"
    audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Depth 3

$federatedCredential | az ad app federated-credential create --id $appId --parameters "@-"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create federated credential for main branch"
    exit 1
}
Write-Success "Created federated credential for main branch"

Write-Step "Creating federated credential for workflow_dispatch..."
$federatedCredentialDispatch = @{
    name = "workflow-dispatch"
    issuer = "https://token.actions.githubusercontent.com"
    subject = "repo:$GitHubRepo`:ref:refs/heads/main"
    audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Depth 3

# Also add support for manual workflow runs
$federatedCredentialManual = @{
    name = "manual-workflow"
    issuer = "https://token.actions.githubusercontent.com"
    subject = "repo:$GitHubRepo"
    audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Depth 3

$federatedCredentialManual | az ad app federated-credential create --id $appId --parameters "@-"
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Failed to create federated credential for manual workflows"
}

Write-Host ""
Write-Host "${Green}🎉 Setup Complete!${Reset}"
Write-Host ""
Write-Host "${Blue}📋 GitHub Secrets to Add:${Reset}"
Write-Host "Go to: https://github.com/$GitHubRepo/settings/secrets/actions"
Write-Host ""
Write-Host "${Yellow}AZURE_CLIENT_ID${Reset}     = $appId"
Write-Host "${Yellow}AZURE_TENANT_ID${Reset}     = $tenantId"
Write-Host "${Yellow}AZURE_SUBSCRIPTION_ID${Reset} = $SubscriptionId"
Write-Host ""

# Check if GitHub CLI is available to automate secret creation
$ghInstalled = $false
try {
    $null = gh --version 2>$null
    if ($LASTEXITCODE -eq 0) {
        $ghInstalled = $true
    }
} catch {}

if ($ghInstalled) {
    Write-Host "${Blue}🤖 Detected GitHub CLI! Would you like to automatically add these secrets?${Reset}"
    $response = Read-Host "Enter 'y' to proceed, or any other key to skip"
    
    if ($response -eq 'y' -or $response -eq 'Y') {
        Write-Step "Adding secrets to GitHub repository..."
        
        try {
            gh secret set AZURE_CLIENT_ID -b $appId -R $GitHubRepo
            gh secret set AZURE_TENANT_ID -b $tenantId -R $GitHubRepo  
            gh secret set AZURE_SUBSCRIPTION_ID -b $SubscriptionId -R $GitHubRepo
            
            Write-Success "Successfully added all secrets to GitHub repository!"
        } catch {
            Write-Warning "Failed to add secrets automatically. Please add them manually."
        }
    }
} else {
    Write-Host "${Blue}💡 Tip: Install GitHub CLI (gh) to automatically add secrets next time!${Reset}"
    Write-Host "Install from: https://cli.github.com/"
}

Write-Host ""
Write-Host "${Blue}🔍 Next Steps:${Reset}"
Write-Host "1. Verify the secrets are added to your GitHub repository"
Write-Host "2. Push to main branch to trigger the deployment workflow"
Write-Host "3. Monitor the GitHub Actions run for any issues"
Write-Host ""
Write-Host "${Green}Happy deploying! 🚀${Reset}"
