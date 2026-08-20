// Stowhaven – main deployment orchestrator
// Deploys to the existing resource group specified at deployment time.
// Usage:
//   az deployment group create \
//     --resource-group <resource-group> \
//     --template-file deploy/bicep/main.bicep \
//     --parameters deploy/bicep/main.bicepparam

targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Azure region for all resources')
param location string = 'westeurope'

@description('Suffix for resource names (with dashes)')
@minLength(1)
param nameSuffix string = 'stowhaven-weu-dev'

@description('Suffix for storage-account-style names (no dashes)')
@minLength(2)
param nameSuffixStr string = 'stowhavenweudev'

@description('Days after which blobs are moved to archive tier')
param lifecycleArchiveAfterDays int = 30

@description('Retention period for Log Analytics workspace in days')
param logAnalyticsRetentionDays int = 30

@description('Daily ingestion quota for Log Analytics workspace in GB')
param logAnalyticsDailyQuotaGb int = 1

@description('Container image tag to deploy')
param imageTag string = 'latest'

@description('Container image registry path that service image names are appended to. GHCR requires a lowercase repository path.')
param containerImageRegistry string = 'ghcr.io/your-github-owner/stowhaven'

@description('Username owning the GHCR pull token. Only used when ghcrPullToken is provided.')
param ghcrPullUsername string = ''

@description('GitHub token with read:packages used by Container Apps to pull images. Injected by the deploy workflow from the GHCR_PULL_TOKEN secret; leave empty when the packages are public.')
@secure()
param ghcrPullToken string = ''

@description('Deploy Container Apps and their runtime role assignments. Authentication inputs must also be complete. Defaults to false for a safe foundation-only deployment.')
param deployContainerApps bool = false

@description('Explicitly allow copy/delete fallback when ADLS Gen2 rename fails. Keep false in production unless early deletion cost and partial-failure risks are accepted.')
param allowCopyDeleteFallback bool = false

@description('Restrict upload SAS URLs to the API-observed client IP. Keep false for SaaS clients unless proxy/client IP behavior has been validated.')
param enableSasIpRestriction bool = false

@description('Dapr cron schedule for automatic stale staging cleanup. Use @every syntax or a cron expression supported by the Dapr cron binding.')
param staleStagingCleanupCronSchedule string = '@every 24h'

@description('Delete staging blobs older than this many hours during scheduled cleanup.')
param staleStagingCleanupOlderThanHours int = 24

@description('Maximum number of stale staging blobs deleted by one scheduled cleanup invocation.')
param staleStagingCleanupMaxDeletes int = 500

@description('Run scheduled stale staging cleanup as a dry run instead of deleting blobs.')
param staleStagingCleanupDryRun bool = false

@description('Minimum API replicas. Use 0 for beta/development cost reduction; set at least 1 when Dapr cron bindings must fire without external traffic.')
param apiMinReplicas int = 0

@description('Minimum Gateway replicas. Keep 0 for lowest cost; the app scales up on HTTP requests.')
param gatewayMinReplicas int = 0

@description('Microsoft Entra application client ID for Container Apps built-in authentication on the Gateway. Required when deployContainerApps is true.')
param gatewayAuthClientId string = ''

@description('Microsoft Entra application client secret for Container Apps built-in authentication on the Gateway. Required when deployContainerApps is true.')
@secure()
param gatewayAuthClientSecret string = ''

@description('Optional allowed token audiences for Gateway built-in auth. Defaults to the Gateway auth client ID when auth is enabled.')
param gatewayAuthAllowedAudiences array = []

@description('Header name used by the Gateway to access otherwise hidden service Swagger endpoints.')
param gatewayProxyHeaderName string = 'X-Backup-Gateway'

@description('Optional shared header value used by the Gateway to access otherwise hidden service Swagger endpoints. Leave empty to derive a stable deployment-specific value.')
@secure()
param gatewayProxyHeaderValue string = ''

@description('Name of the existing, manually created Cosmos DB account used by the production application state repositories. Leave empty to derive from the deployment name suffix.')
param cosmosAccountName string = ''

@description('Cosmos DB SQL database name for application state.')
param cosmosDatabaseName string = 'backup-state'

@description('Cosmos DB shared database autoscale max throughput in RU/s for the backup state containers. Commits burst toward this ceiling; the database idles at 10% of it. NOTE: the Cosmos account (an existing resource, not managed here) carries a totalThroughputLimit guardrail — currently 4000 RU/s — which must stay above this value.')
@minValue(1000)
param cosmosDatabaseAutoscaleMaxThroughput int = 1000

@description('Cosmos DB SQL container name for manifests and commit jobs.')
param cosmosManifestContainerName string = 'manifest-state'

@description('Cosmos DB SQL container name for the device registry.')
param cosmosDeviceRegistryContainerName string = 'device-registry'

@description('Optional Azure client ID for a user-assigned managed identity used by Dapr Azure components. Leave empty for system-assigned Container App identities.')
param daprAzureClientId string = ''

@description('Key Vault network ACL default action. Keep Allow until Container Apps/Dapr access through private networking has been configured; set Deny with private endpoints/VNet integration.')
@allowed([
  'Allow'
  'Deny'
])
param keyVaultNetworkDefaultAction string = 'Allow'

@description('Microsoft Entra tenant ID used by the Stowhaven API for JWT validation.')
param apiAuthTenantId string = tenant().tenantId

@description('Microsoft Entra application/client ID of the Stowhaven API app registration.')
param apiAuthClientId string = ''

@description('JWT audience accepted by the Stowhaven API. Defaults to api://{apiAuthClientId}.')
param apiAuthAudience string = empty(apiAuthClientId) ? '' : 'api://${apiAuthClientId}'

var deployAuthenticatedContainerApps = deployContainerApps && !empty(gatewayAuthClientId) && !empty(gatewayAuthClientSecret) && !empty(apiAuthClientId) && !empty(apiAuthAudience)

// ---------------------------------------------------------------------------
// Locals / derived values
// ---------------------------------------------------------------------------

var commonTags = {
  Environment: 'Production'
  Project: 'Stowhaven'
}

// Pre-determine the Key Vault name so both dapr-infra and compute modules can
// reference it without creating a circular dependency.
var keyVaultName = 'kv-${nameSuffix}'
var cosmosAccountNameEffective = empty(cosmosAccountName) ? 'cosmos-${nameSuffix}' : cosmosAccountName
var dataStorageAccountName = 'stabackup${nameSuffixStr}'
// Role definition IDs (built-in)
var roleStorageBlobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var roleStorageBlobDelegator       = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a')
var roleStorageQueueDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var roleKeyVaultSecretsUser        = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

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

// Deploy shared secret infrastructure (Key Vault).
// The Key Vault name is fixed in advance so compute.bicep can reference it.
module daprInfra 'modules/dapr-infra.bicep' = {
  name: 'dapr-infra'
  params: {
    location: location
    keyVaultName: keyVaultName
    tenantId: tenant().tenantId
    keyVaultNetworkDefaultAction: keyVaultNetworkDefaultAction
    tags: commonTags
  }
}

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: cosmosAccountNameEffective
}

resource dataStorageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: dataStorageAccountName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
    options: {
      autoscaleSettings: {
        maxThroughput: cosmosDatabaseAutoscaleMaxThroughput
      }
    }
  }
}

resource cosmosManifestContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: cosmosManifestContainerName
  properties: {
    resource: {
      id: cosmosManifestContainerName
      partitionKey: {
        paths: [
          '/partitionKey'
        ]
        kind: 'Hash'
      }
    }
  }
}

resource cosmosDeviceRegistryContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: cosmosDeviceRegistryContainerName
  properties: {
    resource: {
      id: cosmosDeviceRegistryContainerName
      partitionKey: {
        paths: [
          '/partitionKey'
        ]
        kind: 'Hash'
      }
    }
  }
}

// Deploy Container App after monitoring, storage, and dapr-infra.
module compute 'modules/compute.bicep' = if (deployAuthenticatedContainerApps) {
  name: 'compute'
  params: {
    location: location
    nameSuffix: nameSuffix
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    containerImageRegistry: containerImageRegistry
    ghcrPullUsername: ghcrPullUsername
    ghcrPullToken: ghcrPullToken
    dataStorageAccountName: storage.outputs.dataStorageAccountName
    containerName: storage.outputs.containerName
    keyVaultName: daprInfra.outputs.keyVaultName
    backupEventsQueueName: storage.outputs.backupEventsQueueName
    backupEventsQueueScaleConnectionString: 'DefaultEndpointsProtocol=https;AccountName=${storage.outputs.dataStorageAccountName};AccountKey=${dataStorageAccount.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
    imageTag: imageTag
    allowCopyDeleteFallback: allowCopyDeleteFallback
    enableSasIpRestriction: enableSasIpRestriction
    staleStagingCleanupCronSchedule: staleStagingCleanupCronSchedule
    staleStagingCleanupOlderThanHours: staleStagingCleanupOlderThanHours
    staleStagingCleanupMaxDeletes: staleStagingCleanupMaxDeletes
    staleStagingCleanupDryRun: staleStagingCleanupDryRun
    apiMinReplicas: apiMinReplicas
    gatewayMinReplicas: gatewayMinReplicas
    gatewayAuthClientId: gatewayAuthClientId
    gatewayAuthClientSecret: gatewayAuthClientSecret
    gatewayAuthAllowedAudiences: gatewayAuthAllowedAudiences
    gatewayProxyHeaderName: gatewayProxyHeaderName
    gatewayProxyHeaderValue: gatewayProxyHeaderValue
    cosmosAccountEndpoint: cosmosAccount.properties.documentEndpoint
    cosmosDatabaseName: cosmosDatabaseName
    cosmosManifestContainerName: cosmosManifestContainerName
    cosmosDeviceRegistryContainerName: cosmosDeviceRegistryContainerName
    daprAzureClientId: daprAzureClientId
    apiAuthTenantId: apiAuthTenantId
    apiAuthClientId: apiAuthClientId
    apiAuthAudience: apiAuthAudience
    tags: commonTags
  }
  dependsOn: [
    cosmosManifestContainer
    cosmosDeviceRegistryContainer
  ]
}

// ---------------------------------------------------------------------------
// Role assignments (after compute provides the managed identity principal ID)
// ---------------------------------------------------------------------------

resource roleAssignStorageContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAuthenticatedContainerApps) {
  name: guid(dataStorageAccount.id, 'ca-${nameSuffix}', 'storage-contributor')
  scope: dataStorageAccount
  properties: {
    roleDefinitionId: roleStorageBlobDataContributor
    principalId: compute!.outputs.principalId
    principalType: 'ServicePrincipal'
    description: 'Container App – Storage Blob Data Contributor on data storage account'
  }
}

resource roleAssignWorkerStorageContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAuthenticatedContainerApps) {
  name: guid(dataStorageAccount.id, 'ca-${nameSuffix}-worker', 'storage-contributor')
  scope: dataStorageAccount
  properties: {
    roleDefinitionId: roleStorageBlobDataContributor
    principalId: compute!.outputs.workerPrincipalId
    principalType: 'ServicePrincipal'
    description: 'Worker Container App – Storage Blob Data Contributor on data storage account'
  }
}

resource roleAssignStorageDelegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAuthenticatedContainerApps) {
  name: guid(dataStorageAccount.id, 'ca-${nameSuffix}', 'storage-delegator')
  scope: dataStorageAccount
  properties: {
    roleDefinitionId: roleStorageBlobDelegator
    principalId: compute!.outputs.principalId
    principalType: 'ServicePrincipal'
    description: 'Container App – Storage Blob Delegator on data storage account'
  }
}

resource roleAssignStorageQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAuthenticatedContainerApps) {
  name: guid(dataStorageAccount.id, 'ca-${nameSuffix}', 'queue-contributor')
  scope: dataStorageAccount
  properties: {
    roleDefinitionId: roleStorageQueueDataContributor
    principalId: compute!.outputs.principalId
    principalType: 'ServicePrincipal'
    description: 'Container App – Storage Queue Data Contributor on backup events queue'
  }
}

resource roleAssignWorkerStorageQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAuthenticatedContainerApps) {
  name: guid(dataStorageAccount.id, 'ca-${nameSuffix}-worker', 'queue-contributor')
  scope: dataStorageAccount
  properties: {
    roleDefinitionId: roleStorageQueueDataContributor
    principalId: compute!.outputs.workerPrincipalId
    principalType: 'ServicePrincipal'
    description: 'Worker Container App – Storage Queue Data Contributor on backup events queue'
  }
}

resource roleAssignKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAuthenticatedContainerApps) {
  name: guid(keyVault.id, 'ca-${nameSuffix}', 'kv-secrets-user')
  scope: keyVault
  properties: {
    roleDefinitionId: roleKeyVaultSecretsUser
    principalId: compute!.outputs.principalId
    principalType: 'ServicePrincipal'
    description: 'Container App - Key Vault Secrets User'
  }
}

resource roleAssignWorkerKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAuthenticatedContainerApps) {
  name: guid(keyVault.id, 'ca-${nameSuffix}-worker', 'kv-secrets-user')
  scope: keyVault
  properties: {
    roleDefinitionId: roleKeyVaultSecretsUser
    principalId: compute!.outputs.workerPrincipalId
    principalType: 'ServicePrincipal'
    description: 'Worker Container App - Key Vault Secrets User'
  }
}

resource roleAssignCosmosDataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = if (deployAuthenticatedContainerApps) {
  name: guid(resourceGroup().id, cosmosAccount.name, 'ca-${nameSuffix}', 'cosmos-data-contributor')
  parent: cosmosAccount
  properties: {
    roleDefinitionId: resourceId('Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions', cosmosAccount.name, '00000000-0000-0000-0000-000000000002')
    principalId: compute!.outputs.principalId
    scope: cosmosAccount.id
  }
}

resource roleAssignWorkerCosmosDataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = if (deployAuthenticatedContainerApps) {
  name: guid(resourceGroup().id, cosmosAccount.name, 'ca-${nameSuffix}-worker', 'cosmos-data-contributor')
  parent: cosmosAccount
  properties: {
    roleDefinitionId: resourceId('Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions', cosmosAccount.name, '00000000-0000-0000-0000-000000000002')
    principalId: compute!.outputs.workerPrincipalId
    scope: cosmosAccount.id
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output containerAppsDeployed bool = deployAuthenticatedContainerApps
output containerAppName string = deployAuthenticatedContainerApps ? compute!.outputs.containerAppName : ''
output containerAppUrl string = deployAuthenticatedContainerApps ? 'https://${compute!.outputs.containerAppFqdn}' : ''
output workerContainerAppName string = deployAuthenticatedContainerApps ? compute!.outputs.workerContainerAppName : ''
output gatewayContainerAppName string = deployAuthenticatedContainerApps ? compute!.outputs.gatewayContainerAppName : ''
output gatewayContainerAppUrl string = deployAuthenticatedContainerApps ? 'https://${compute!.outputs.gatewayContainerAppFqdn}' : ''
output gatewayPrincipalId string = deployAuthenticatedContainerApps ? compute!.outputs.gatewayPrincipalId : ''
output dataStorageAccountName string = storage.outputs.dataStorageAccountName
output containerName string = storage.outputs.containerName
output logAnalyticsWorkspaceName string = monitoring.outputs.workspaceName
output appInsightsName string = monitoring.outputs.appInsightsName
