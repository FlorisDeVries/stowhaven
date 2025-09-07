param location string = 'westeurope'
param lifecycleArchiveAfterDays int = 30
param nameSuffix string = 'fdev-weu-prd'
param nameSuffixStr string = 'fdevweuprd'

// Storage voor data
var dataSaName = toLower('stabackup${nameSuffixStr}')
resource dataSa 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: dataSaName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    accessTier: 'Cold'
  }
}

// Container voor backups
resource dataContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${dataSa.name}/default/backups'
  properties: {
    publicAccess: 'None'
  }
}

// Lifecycle: naar Archive na X dagen niet gewijzigd
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2021-04-01' = {
  parent: dataSa
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          enabled: true
          name: 'to-archive-after-days'
          type: 'Lifecycle'
          definition: {
            actions: {
              baseBlob: {
                tierToArchive: {
                  daysAfterModificationGreaterThan: lifecycleArchiveAfterDays
                }
              }
            }
            filters: {
              blobTypes: [ 'blockBlob' ]
              prefixMatch: [ 'backups/' ]
            }
          }
        }
      ]
    }
  }
}

// Function storage (consumption plan vereist dit)
var funcSaName = toLower('stafunc${nameSuffixStr}')
resource funcSa 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: funcSaName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${nameSuffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    IngestionMode: 'LogAnalytics'
  }
}

resource funcApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${nameSuffix}'
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    siteConfig: {
      linuxFxVersion: 'Python|3.11'
      appSettings: [
        { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${funcSa.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${funcSa.listKeys().keys[0].value}' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'python' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'APPINSIGHTS_INSTRUMENTATIONKEY', value: appInsights.properties.InstrumentationKey }
        { name: 'DATA_STORAGE_ACCOUNT', value: dataSa.name }
        { name: 'DATA_CONTAINER', value: 'backups' }
      ]
      http20Enabled: true
      alwaysOn: false
    }
    serverFarmId: resourceId('Microsoft.Web/serverfarms', 'plan-${nameSuffix}')
    httpsOnly: true
  
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${nameSuffix}'
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'functionapp'
}

resource roleAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(funcApp.id, 'blob-contributor', dataSa.id)
  scope: dataSa
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe' // Storage Blob Data Contributor
    )
    principalId: funcApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output dataStorageAccountName string = dataSa.name
output containerName string = dataContainer.name
output functionAppName string = funcApp.name
