// Storage accounts, backup container, and lifecycle policy

@description('Azure region for resources')
param location string

@description('Suffix for storage account names (no dashes, e.g. fdevneuprd)')
param nameSuffixStr string

@description('Days after which blobs are moved to archive tier')
param lifecycleArchiveAfterDays int

@description('Common resource tags')
param tags object

// ---------------------------------------------------------------------------
// Data storage account
// ---------------------------------------------------------------------------

resource dataStorageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: 'stabackup${nameSuffixStr}'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Cool'
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
  tags: tags
}

resource dataStorageBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: dataStorageAccount
  name: 'default'
}

resource backupsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: dataStorageBlobService
  name: 'backups'
  properties: {
    publicAccess: 'None'
  }
}

resource lifecyclePolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2023-01-01' = {
  parent: dataStorageAccount
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'to-archive-after-days'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              prefixMatch: ['backups/']
              blobTypes: ['blockBlob']
            }
            actions: {
              baseBlob: {
                tierToArchive: {
                  daysAfterModificationGreaterThan: lifecycleArchiveAfterDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output dataStorageAccountName string = dataStorageAccount.name
output dataStorageAccountId string = dataStorageAccount.id
output containerName string = backupsContainer.name
