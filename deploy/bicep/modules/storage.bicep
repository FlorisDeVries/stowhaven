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
    accessTier: 'Cool'  // Account default; blobs can be set to Cold tier individually
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    isHnsEnabled: true  // Enable hierarchical namespace for ADLS Gen2 (directory-scoped SAS & rename)
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
          name: 'devices-to-cold'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              prefixMatch: ['devices/']
              blobTypes: ['blockBlob']
            }
            actions: {
              baseBlob: {
                tierToCold: {
                  daysAfterCreationGreaterThan: 0  // Move to Cold tier immediately if not already
                }
              }
            }
          }
        }
        {
          name: 'backup-tier-promotion'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              prefixMatch: ['devices/']
              blobTypes: ['blockBlob']
            }
            actions: {
              baseBlob: {
                tierToArchive: {
                  daysAfterCreationGreaterThan: lifecycleArchiveAfterDays
                }
                delete: {
                  daysAfterCreationGreaterThan: 210  // 180 (archive min) + 30 (grace)
                }
              }
            }
          }
        }
        {
          name: 'retired-cleanup'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              prefixMatch: ['devices/']
              blobIndexMatch: [
                {
                  name: 'state'
                  op: '=='
                  value: 'retired'
                }
              ]
              blobTypes: ['blockBlob']
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterCreationGreaterThan: 210
                }
              }
            }
          }
        }
        {
          name: 'staging-cleanup'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              prefixMatch: ['staging/']
              blobTypes: ['blockBlob']
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterCreationGreaterThan: 7  // Clean up stale uploads after 7 days
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
