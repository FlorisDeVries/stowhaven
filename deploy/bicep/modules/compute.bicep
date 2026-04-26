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

@description('API key secret value (stored as a Container App secret)')
@secure()
param apiKey string

@description('Container image tag to deploy')
param imageTag string = 'latest'

@description('Common resource tags')
param tags object

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
    metadata: [
      {
        name: 'vaultName'
        value: keyVaultName
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Dapr component: manifest state-store (Azure Table Storage via managed identity)
// ---------------------------------------------------------------------------

resource daprManifestStateStore 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppEnv
  name: 'manifest-state-store'
  properties: {
    componentType: 'state.azure.tablestorage'
    version: 'v1'
    metadata: [
      {
        name: 'accountName'
        value: dataStorageAccountName
      }
      {
        name: 'tableName'
        value: 'manifeststate'
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Dapr component: device registry state-store (Azure Table Storage via managed identity)
// ---------------------------------------------------------------------------

resource daprDeviceRegistryStateStore 'Microsoft.App/managedEnvironments/daprComponents@2023-05-01' = {
  parent: containerAppEnv
  name: 'device-registry-state-store'
  properties: {
    componentType: 'state.azure.tablestorage'
    version: 'v1'
    metadata: [
      {
        name: 'accountName'
        value: dataStorageAccountName
      }
      {
        name: 'tableName'
        value: 'deviceregistry'
      }
    ]
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
    metadata: [
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
    type: 'SystemAssigned'
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
          identity: 'system' // Use system-assigned managed identity for ACR pull
        }
      ]
      secrets: [
        {
          name: 'api-key'
          value: apiKey
        }
      ]
    }
    template: {
      scale: {
        minReplicas: 0
        maxReplicas: 10
      }
      containers: [
        {
          name: 'backup-api'
          image: '${registryLoginServer}/backup-api:${imageTag}'
          resources: {
            cpu: 1
            memory: '0.5Gi'
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
              name: 'API_KEY'
              secretRef: 'api-key'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
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
    type: 'SystemAssigned'
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
          identity: 'system'
        }
      ]
      secrets: [
        {
          name: 'api-key'
          value: apiKey
        }
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
            cpu: 1
            memory: '0.5Gi'
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
              name: 'API_KEY'
              secretRef: 'api-key'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
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
