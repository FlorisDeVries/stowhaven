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

@description('Storage Queue name for backup events.')
param backupEventsQueueName string = 'backup-events'

@description('Storage account connection string used only by the Container Apps queue scaler.')
@secure()
param backupEventsQueueScaleConnectionString string

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

@description('Minimum API replicas. Use 0 for beta/development cost reduction; set at least 1 when Dapr cron bindings must fire without external traffic.')
param apiMinReplicas int = 0

@description('Minimum Gateway replicas. Keep 0 for lowest cost; the app scales up on HTTP requests.')
param gatewayMinReplicas int = 0

@description('Optional Microsoft Entra application client ID for Container Apps built-in authentication on the Gateway. Leave empty to deploy without built-in auth.')
param gatewayAuthClientId string = ''

@description('Optional Microsoft Entra application client secret for Container Apps built-in authentication on the Gateway.')
@secure()
param gatewayAuthClientSecret string = ''

@description('Optional allowed token audiences for Gateway built-in auth. Defaults to the Gateway auth client ID when auth is enabled.')
param gatewayAuthAllowedAudiences array = []

@description('SAS URL for the Blob Storage container used by Container Apps Easy Auth token store.')
@secure()
param gatewayAuthTokenStoreSasUrl string = ''

@description('Header name used by the Gateway to access otherwise hidden service Swagger endpoints.')
param gatewayProxyHeaderName string = 'X-Backup-Gateway'

@description('Optional shared header value used by the Gateway to access otherwise hidden service Swagger endpoints. Leave empty to derive a stable deployment-specific value.')
@secure()
param gatewayProxyHeaderValue string = ''

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

@description('Microsoft Entra tenant ID used by the Backup API for JWT validation.')
param apiAuthTenantId string = tenant().tenantId

@description('Microsoft Entra application/client ID of the Backup API app registration.')
param apiAuthClientId string = '906eb0e3-e351-47c0-a68a-690207f4cccb'

@description('JWT audience accepted by the Backup API. Defaults to api://{apiAuthClientId}.')
param apiAuthAudience string = 'api://${apiAuthClientId}'

var gatewayAuthEnabled = !empty(gatewayAuthClientId) && !empty(gatewayAuthClientSecret)
var gatewayAuthTokenStoreSasUrlSecretName = 'gateway-auth-token-store-sas-url'
var gatewayProxyHeaderValueEffective = empty(gatewayProxyHeaderValue) ? uniqueString(subscription().id, resourceGroup().id, nameSuffix, 'gateway') : gatewayProxyHeaderValue
var gatewayAuthSecrets = gatewayAuthEnabled ? [
  {
    name: 'gateway-auth-client-secret'
    value: gatewayAuthClientSecret
  }
  {
    name: gatewayAuthTokenStoreSasUrlSecretName
    value: gatewayAuthTokenStoreSasUrl
  }
] : []

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
// Dapr components
// ---------------------------------------------------------------------------

module daprComponents 'dapr-components.bicep' = {
  name: 'dapr-components'
  params: {
    managedEnvironmentName: containerAppEnv.name
    keyVaultName: keyVaultName
    cosmosAccountEndpoint: cosmosAccountEndpoint
    cosmosDatabaseName: cosmosDatabaseName
    cosmosManifestContainerName: cosmosManifestContainerName
    cosmosDeviceRegistryContainerName: cosmosDeviceRegistryContainerName
    dataStorageAccountName: dataStorageAccountName
    backupEventsQueueName: backupEventsQueueName
    staleStagingCleanupCronSchedule: staleStagingCleanupCronSchedule
    daprAzureClientId: daprAzureClientId
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
        external: false
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
            {
              name: 'AzureAd__TenantId'
              value: apiAuthTenantId
            }
            {
              name: 'AzureAd__ClientId'
              value: apiAuthClientId
            }
            {
              name: 'AzureAd__Audience'
              value: apiAuthAudience
            }
            {
              name: 'DaprHealth__EnablePubSubProbe'
              value: 'false'
            }
            {
              name: 'Swagger__RequiredGatewayHeaderName'
              value: gatewayProxyHeaderName
            }
            {
              name: 'Swagger__RequiredGatewayHeaderValue'
              value: gatewayProxyHeaderValueEffective
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
      ingress: {
        external: false
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
      secrets: [
        {
          name: 'backup-events-queue-scale-connection-string'
          value: backupEventsQueueScaleConnectionString
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
              type: 'azure-queue'
              metadata: {
                accountName: dataStorageAccountName
                queueName: backupEventsQueueName
                queueLength: '1'
              }
              auth: [
                {
                  secretRef: 'backup-events-queue-scale-connection-string'
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
            {
              name: 'DaprHealth__EnablePubSubProbe'
              value: 'false'
            }
            {
              name: 'Swagger__RequiredGatewayHeaderName'
              value: gatewayProxyHeaderName
            }
            {
              name: 'Swagger__RequiredGatewayHeaderValue'
              value: gatewayProxyHeaderValueEffective
            }
          ]
        }
      ]
    }
  }
  tags: tags
}

resource gatewayContainerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'ca-${nameSuffix}-gateway'
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
      secrets: gatewayAuthSecrets
    }
    template: {
      scale: {
        minReplicas: gatewayMinReplicas
        maxReplicas: 2
      }
      containers: [
        {
          name: 'gateway'
          image: '${registryLoginServer}/gateway:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'Gateway__ApiBaseUrl'
              value: 'https://${containerApp.properties.configuration.ingress.fqdn}'
            }
            {
              name: 'Gateway__WorkerBaseUrl'
              value: 'https://${workerContainerApp.properties.configuration.ingress.fqdn}'
            }
            {
              name: 'Gateway__HeaderName'
              value: gatewayProxyHeaderName
            }
            {
              name: 'Gateway__HeaderValue'
              value: gatewayProxyHeaderValueEffective
            }
            {
              name: 'Gateway__ApiTokenScope'
              value: '${apiAuthAudience}/.default'
            }
            ...(gatewayAuthEnabled ? [
              {
                name: 'Gateway__OboClientId'
                value: gatewayAuthClientId
              }
              {
                name: 'Gateway__OboTenantId'
                value: apiAuthTenantId
              }
              {
                name: 'Gateway__OboClientSecret'
                secretRef: 'gateway-auth-client-secret'
              }
            ] : [])
          ]
        }
      ]
    }
  }
  tags: tags
}

resource gatewayAuthConfig 'Microsoft.App/containerApps/authConfigs@2024-03-01' = if (gatewayAuthEnabled) {
  parent: gatewayContainerApp
  name: 'current'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
    }
    login: {
      tokenStore: {
        enabled: true
        azureBlobStorage: {
          sasUrlSettingName: gatewayAuthTokenStoreSasUrlSecretName
        }
      }
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: gatewayAuthClientId
          clientSecretSettingName: 'gateway-auth-client-secret'
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: empty(gatewayAuthAllowedAudiences) ? [
            gatewayAuthClientId
            'api://${gatewayAuthClientId}'
            apiAuthClientId
            apiAuthAudience
          ] : gatewayAuthAllowedAudiences
        }
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output containerAppName string = containerApp.name
output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output principalId string = containerApp.identity.principalId
output workerContainerAppName string = workerContainerApp.name
output workerContainerAppFqdn string = workerContainerApp.properties.configuration.ingress.fqdn
output workerPrincipalId string = workerContainerApp.identity.principalId
output gatewayContainerAppName string = gatewayContainerApp.name
output gatewayContainerAppFqdn string = gatewayContainerApp.properties.configuration.ingress.fqdn
output gatewayPrincipalId string = gatewayContainerApp.identity.principalId
