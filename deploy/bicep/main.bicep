// Backup API – main deployment orchestrator
// Deploys to the existing resource group specified at deployment time.
// Usage:
//   az deployment group create \
//     --resource-group rg-fdev-neu-backup-prd \
//     --template-file deploy/bicep/main.bicep \
//     --parameters deploy/bicep/main.bicepparam

targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Azure region for all resources')
param location string = 'northeurope'

@description('Suffix for resource names (with dashes)')
param nameSuffix string = 'fdev-neu-prd'

@description('Suffix for storage-account-style names (no dashes)')
param nameSuffixStr string = 'fdevneuprd'

@description('Days after which blobs are moved to archive tier')
param lifecycleArchiveAfterDays int = 30

@description('API key for authenticating requests to the Backup API')
@secure()
param apiKey string

@description('Retention period for Log Analytics workspace in days')
param logAnalyticsRetentionDays int = 30

@description('Daily ingestion quota for Log Analytics workspace in GB')
param logAnalyticsDailyQuotaGb int = 1

@description('Container image tag to deploy')
param imageTag string = 'latest'

// ---------------------------------------------------------------------------
// Locals / derived values
// ---------------------------------------------------------------------------

var commonTags = {
  Environment: 'Production'
  Project: 'BackupAPI'
}

// Pre-determine the Key Vault name so both dapr-infra and compute modules can
// reference it without creating a circular dependency.
var keyVaultName = 'kv-${nameSuffix}'

// Role definition IDs (built-in)
var roleStorageBlobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var roleStorageBlobDelegator       = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a')
var roleAcrPull                    = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var roleKeyVaultSecretsUser        = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e0')

// ---------------------------------------------------------------------------
// Modules
// ---------------------------------------------------------------------------

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    nameSuffixStr: nameSuffixStr
    lifecycleArchiveAfterDays: lifecycleArchiveAfterDays
    tags: commonTags
  }
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    nameSuffix: nameSuffix
    retentionDays: logAnalyticsRetentionDays
    dailyQuotaGb: logAnalyticsDailyQuotaGb
    tags: commonTags
  }
}

module registry 'modules/registry.bicep' = {
  name: 'registry'
  params: {
    location: location
    nameSuffixStr: nameSuffixStr
    tags: commonTags
  }
}

// Deploy Dapr infrastructure (Redis, Service Bus, Key Vault).
// The Key Vault name is fixed in advance so compute.bicep can reference it.
module daprInfra 'modules/dapr-infra.bicep' = {
  name: 'dapr-infra'
  params: {
    location: location
    nameSuffix: nameSuffix
    keyVaultName: keyVaultName
    tenantId: tenant().tenantId
    tags: commonTags
  }
}

// Deploy Container App after monitoring, registry, storage, and dapr-infra.
module compute 'modules/compute.bicep' = {
  name: 'compute'
  params: {
    location: location
    nameSuffix: nameSuffix
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    registryLoginServer: registry.outputs.loginServer
    dataStorageAccountName: storage.outputs.dataStorageAccountName
    containerName: storage.outputs.containerName
    keyVaultName: daprInfra.outputs.keyVaultName
    apiKey: apiKey
    imageTag: imageTag
    tags: commonTags
  }
}

// ---------------------------------------------------------------------------
// Role assignments (after compute provides the managed identity principal ID)
// ---------------------------------------------------------------------------

resource roleAssignStorageContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, 'stabackup${nameSuffixStr}', 'ca-${nameSuffix}', 'storage-contributor')
  scope: resourceGroup()
  properties: {
    roleDefinitionId: roleStorageBlobDataContributor
    principalId: compute.outputs.principalId
    principalType: 'ServicePrincipal'
    description: 'Container App – Storage Blob Data Contributor on data storage account'
  }
}

resource roleAssignStorageDelegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, 'stabackup${nameSuffixStr}', 'ca-${nameSuffix}', 'storage-delegator')
  scope: resourceGroup()
  properties: {
    roleDefinitionId: roleStorageBlobDelegator
    principalId: compute.outputs.principalId
    principalType: 'ServicePrincipal'
    description: 'Container App – Storage Blob Delegator on data storage account'
  }
}

resource roleAssignAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, 'acr${nameSuffixStr}', 'ca-${nameSuffix}', 'acr-pull')
  scope: resourceGroup()
  properties: {
    roleDefinitionId: roleAcrPull
    principalId: compute.outputs.principalId
    principalType: 'ServicePrincipal'
    description: 'Container App – AcrPull on container registry'
  }
}

resource roleAssignKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, keyVaultName, 'ca-${nameSuffix}', 'kv-secrets-user')
  scope: resourceGroup()
  properties: {
    roleDefinitionId: roleKeyVaultSecretsUser
    principalId: compute.outputs.principalId
    principalType: 'ServicePrincipal'
    description: 'Container App – Key Vault Secrets User'
  }
}

// ---------------------------------------------------------------------------
// Key Vault secrets (created after IAM role assignments are in place)
// ---------------------------------------------------------------------------

resource kvRef 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource kvSecretApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kvRef
  name: 'api-key'
  properties: {
    value: apiKey
  }
  dependsOn: [roleAssignKeyVaultSecretsUser]
}

resource kvSecretStorageAccount 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kvRef
  name: 'storage-account-name'
  properties: {
    value: storage.outputs.dataStorageAccountName
  }
  dependsOn: [roleAssignKeyVaultSecretsUser]
}

resource kvSecretContainerName 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kvRef
  name: 'data-container'
  properties: {
    value: storage.outputs.containerName
  }
  dependsOn: [roleAssignKeyVaultSecretsUser]
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output containerAppName string = compute.outputs.containerAppName
output containerAppUrl string = 'https://${compute.outputs.containerAppFqdn}'
output dataStorageAccountName string = storage.outputs.dataStorageAccountName
output containerName string = storage.outputs.containerName
output containerRegistryName string = registry.outputs.name
output containerRegistryLoginServer string = registry.outputs.loginServer
output logAnalyticsWorkspaceName string = monitoring.outputs.workspaceName
output appInsightsName string = monitoring.outputs.appInsightsName
