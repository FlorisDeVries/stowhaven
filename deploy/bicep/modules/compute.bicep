// Container App Environment, Container App, and Dapr secret-store component

@description('Azure region for resources')
param location string

@description('Suffix for resource names (with dashes)')
param nameSuffix string

@description('Log Analytics workspace resource ID')
param logAnalyticsWorkspaceId string

@description('Application Insights connection string')
@secure()
param appInsightsConnectionString string

@description('Container registry login server')
param registryLoginServer string

@description('Resource ID of the user-assigned managed identity used by Container Apps to pull images from ACR')
param registryPullIdentityId string

@description('Storage account name for data blobs')
param dataStorageAccountName string

@description('Blob container name')
param containerName string

@description('Key Vault name for Dapr secret-store component')
param keyVaultName string

@description('Service Bus namespace name for Dapr pub/sub component')
param serviceBusNamespaceName string

@description('Least-privilege Service Bus Listen connection string used only by the Container Apps scaler')
@secure()
param serviceBusScaleConnectionString string

@description('Container image tag to deploy')
param imageTag string = 'latest'

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

@description('Minimum API replicas. Keep at least 1 when Dapr cron bindings must fire without external traffic.')
param apiMinReplicas int = 1

@description('Cosmos DB account endpoint for the Dapr manifest-state-store component.')
param cosmosAccountEndpoint string

@description('Cosmos DB SQL database name for Dapr state.')
param cosmosDatabaseName string = 'backup-state'

@description('Cosmos DB SQL container name for manifest-state-store.')
param cosmosManifestContainerName string = 'manifest-state'

@description('Cosmos DB SQL container name for device-registry-state-store.')
param cosmosDeviceRegistryContainerName string = 'device-registry'

@description('Optional Azure client ID for a user-assigned managed identity used by Dapr Azure components. Leave empty for system-assigned Container App identities.')
param daprAzureClientId string = ''

@description('Common resource tags')
param tags object

var daprAzureIdentityMetadata = empty(daprAzureClientId) ? [] : [
  {
    name: 'azureClientId'
    value: daprAzureClientId
  }
]

// ---------------------------------------------------------------------------
// Container App Environment
// ---------------------------------------------------------------------------

resource containerAppEnv 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: 'cae-${nameSuffix}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(logAnalyticsWorkspaceId, '2022-10-01').customerId
        sharedKey: listKeys(logAnalyticsWorkspaceId, '2022-10-01').primarySharedKey
      }
    }
    daprAIConnectionString: appInsightsConnectionString
  }
  tags: tags
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

// ---------------------------------------------------------------------------
// Dapr component: manifest state-store (Azure Cosmos DB for NoSQL via managed identity)
// ---------------------------------------------------------------------------

resource daprManifestStateStore 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppEnv
  name: 'manifest-state-store'
  properties: {
    componentType: 'state.azure.cosmosdb'
    version: 'v1'
    initTimeout: '5m'
    metadata: concat([
      {
        name: 'url'
        value: cosmosAccountEndpoint
      }
      {
        name: 'database'
        value: cosmosDatabaseName
      }
      {
        name: 'collection'
        value: cosmosManifestContainerName
      }
      {
        name: 'partitionKey'
        value: 'partitionKey'
      }
    ], daprAzureIdentityMetadata)
  }
}

// ---------------------------------------------------------------------------
// Dapr component: device registry state-store (Azure Cosmos DB for NoSQL via managed identity)
// ---------------------------------------------------------------------------

resource daprDeviceRegistryStateStore 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppEnv
  name: 'device-registry-state-store'
  properties: {
    componentType: 'state.azure.cosmosdb'
    version: 'v1'
    initTimeout: '5m'
    metadata: concat([
      {
        name: 'url'
        value: cosmosAccountEndpoint
      }
      {
        name: 'database'
        value: cosmosDatabaseName
      }
      {
        name: 'collection'
        value: cosmosDeviceRegistryContainerName
      }
      {
        name: 'partitionKey'
        value: 'partitionKey'
      }
    ], daprAzureIdentityMetadata)
  }
}

// ---------------------------------------------------------------------------
// Dapr component: pub/sub (Azure Service Bus via managed identity)
// ---------------------------------------------------------------------------

resource daprPubSub 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppEnv
  name: 'backup-events-pubsub'
  properties: {
    componentType: 'pubsub.azure.servicebus.topics'
    version: 'v1'
    metadata: concat([
      {
        name: 'namespaceName'
        value: '${serviceBusNamespaceName}.servicebus.windows.net'
      }
      {
        name: 'consumerID'
        value: 'backup-worker'
      }
      {
        name: 'maxConcurrentHandlers'
        value: '8'
      }
      {
        name: 'timeoutInSec'
        value: '300'
      }
      {
        name: 'maxRetryCount'
        value: '3'
      }
    ], daprAzureIdentityMetadata)
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

// ---------------------------------------------------------------------------
// Container App
// ---------------------------------------------------------------------------

resource containerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'ca-${nameSuffix}'
  location: location
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${registryPullIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      dapr: {
        enabled: true
        appId: 'backup-api'
        appPort: 8080
        appProtocol: 'http'
      }
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        traffic: [
          {
            weight: 100
            latestRevision: true
          }
        ]
      }
      registries: [
        {
          server: registryLoginServer
          identity: registryPullIdentityId
        }
      ]
    }
    template: {
      scale: {
        minReplicas: apiMinReplicas
        maxReplicas: 10
      }
      containers: [
        {
          name: 'backup-api'
          image: '${registryLoginServer}/backup-api:${imageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'DATA_STORAGE_ACCOUNT'
              value: dataStorageAccountName
            }
            {
              name: 'DATA_CONTAINER'
              value: containerName
            }
            {
              name: 'ALLOW_COPY_DELETE_FALLBACK'
              value: string(allowCopyDeleteFallback)
            }
            {
              name: 'Backup__Sas__EnableIpRestriction'
              value: string(enableSasIpRestriction)
            }
            {
              name: 'Operations__StaleStagingCleanup__OlderThanHours'
              value: string(staleStagingCleanupOlderThanHours)
            }
            {
              name: 'Operations__StaleStagingCleanup__MaxDeletes'
              value: string(staleStagingCleanupMaxDeletes)
            }
            {
              name: 'Operations__StaleStagingCleanup__DryRun'
              value: string(staleStagingCleanupDryRun)
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
            {
              name: 'OTEL_EXPORTER_OTLP_ENDPOINT'
              value: ''
            }
            {
              name: 'OTEL_EXPORTER_ZIPKIN_ENDPOINT'
              value: ''
            }
            {
              name: 'OTEL_EXPORTER_AZURE_MONITOR_CONNECTION'
              value: ''
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
          ]
        }
      ]
    }
  }
  tags: tags
}

resource workerContainerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'ca-${nameSuffix}-worker'
  location: location
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${registryPullIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      dapr: {
        enabled: true
        appId: 'backup-worker'
        appPort: 8080
        appProtocol: 'http'
      }
      registries: [
        {
          server: registryLoginServer
          identity: registryPullIdentityId
        }
      ]
      secrets: [
        {
          name: 'servicebus-scale-connection-string'
          value: serviceBusScaleConnectionString
        }
      ]
    }
    template: {
      scale: {
        minReplicas: 0
        maxReplicas: 3
        rules: [
          {
            name: 'backup-events'
            custom: {
              type: 'azure-servicebus'
              metadata: {
                topicName: 'backup-events'
                subscriptionName: 'backup-worker'
                messageCount: '1'
              }
              auth: [
                {
                  secretRef: 'servicebus-scale-connection-string'
                  triggerParameter: 'connection'
                }
              ]
            }
          }
        ]
      }
      containers: [
        {
          name: 'backup-worker'
          image: '${registryLoginServer}/backup-worker:${imageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'DATA_STORAGE_ACCOUNT'
              value: dataStorageAccountName
            }
            {
              name: 'DATA_CONTAINER'
              value: containerName
            }
            {
              name: 'ALLOW_COPY_DELETE_FALLBACK'
              value: string(allowCopyDeleteFallback)
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
            {
              name: 'OTEL_EXPORTER_OTLP_ENDPOINT'
              value: ''
            }
            {
              name: 'OTEL_EXPORTER_ZIPKIN_ENDPOINT'
              value: ''
            }
            {
              name: 'OTEL_EXPORTER_AZURE_MONITOR_CONNECTION'
              value: ''
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
          ]
        }
      ]
    }
  }
  tags: tags
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output containerAppName string = containerApp.name
output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output principalId string = containerApp.identity.principalId
output workerContainerAppName string = workerContainerApp.name
output workerPrincipalId string = workerContainerApp.identity.principalId
