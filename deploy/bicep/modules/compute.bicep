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

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output containerAppName string = containerApp.name
output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output principalId string = containerApp.identity.principalId
