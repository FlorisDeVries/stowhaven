// Dapr components for the Container Apps Environment.

@description('Name of the existing Container Apps Environment that hosts these Dapr components.')
param managedEnvironmentName string

@description('Key Vault name for Dapr secret-store component.')
param keyVaultName string

@description('Storage account name for backup event queue bindings.')
param dataStorageAccountName string

@description('Storage Queue name for backup events.')
param backupEventsQueueName string = 'backup-events'

@description('Dapr cron schedule for automatic stale staging cleanup. Use @every syntax or a cron expression supported by the Dapr cron binding.')
param staleStagingCleanupCronSchedule string = '@every 24h'

@description('Optional Azure client ID for a user-assigned managed identity used by Dapr Azure components. Leave empty for system-assigned Container App identities.')
param daprAzureClientId string = ''

var daprAzureIdentityMetadata = empty(daprAzureClientId) ? [] : [
  {
    name: 'azureClientId'
    value: daprAzureClientId
  }
]

resource containerAppEnv 'Microsoft.App/managedEnvironments@2023-05-01' existing = {
  name: managedEnvironmentName
}

// ---------------------------------------------------------------------------
// Dapr component: secret-store (Azure Key Vault via managed identity)
// ---------------------------------------------------------------------------

resource daprSecretStore 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppEnv
  name: 'secret-store'
  properties: {
    componentType: 'secretstores.azure.keyvault'
    version: 'v1'
    metadata: concat([
      {
        name: 'vaultName'
        value: keyVaultName
      }
    ], daprAzureIdentityMetadata)
  }
}

// Dapr provides secrets, queue bindings, and cron. Application state is handled
// in-app by the IStateDocumentStore repository layer (Cosmos in production,
// SQLite locally), so no state components are defined here.

// ---------------------------------------------------------------------------
// Dapr component: output binding for backup events (Azure Storage Queue via managed identity)
// ---------------------------------------------------------------------------

resource daprBackupEventsOutputBinding 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppEnv
  name: 'backup-events-output'
  properties: {
    componentType: 'bindings.azure.storagequeues'
    version: 'v1'
    metadata: concat([
      {
        name: 'accountName'
        value: dataStorageAccountName
      }
      {
        name: 'queueName'
        value: backupEventsQueueName
      }
      {
        name: 'direction'
        value: 'output'
      }
      {
        name: 'ttlInSeconds'
        value: '604800'
      }
    ], daprAzureIdentityMetadata)
    scopes: [
      'backup-api'
    ]
  }
}

// ---------------------------------------------------------------------------
// Dapr component: input binding for backup events (Azure Storage Queue via managed identity)
// ---------------------------------------------------------------------------

resource daprBackupEventsInputBinding 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppEnv
  name: 'backup-events-input'
  properties: {
    componentType: 'bindings.azure.storagequeues'
    version: 'v1'
    metadata: concat([
      {
        name: 'accountName'
        value: dataStorageAccountName
      }
      {
        name: 'queueName'
        value: backupEventsQueueName
      }
      {
        name: 'direction'
        value: 'input'
      }
      {
        name: 'route'
        value: '/api/backupevents/backup-run-committed'
      }
      {
        name: 'pollingInterval'
        value: '10s'
      }
      {
        name: 'visibilityTimeout'
        value: '10m'
      }
    ], daprAzureIdentityMetadata)
    scopes: [
      'backup-worker'
    ]
  }
}

// ---------------------------------------------------------------------------
// Dapr component: scheduled stale staging cleanup trigger
// ---------------------------------------------------------------------------

resource daprStaleStagingCleanupCron 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppEnv
  name: 'cleanup-staging-cron'
  properties: {
    componentType: 'bindings.cron'
    version: 'v1'
    metadata: [
      {
        name: 'schedule'
        value: staleStagingCleanupCronSchedule
      }
    ]
    scopes: [
      'backup-api'
    ]
  }
}
