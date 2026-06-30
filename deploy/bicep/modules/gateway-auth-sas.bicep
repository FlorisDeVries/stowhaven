// Generates a SAS URL for the gateway Easy Auth token store container.
// All resources are declared as existing — this module must only be deployed
// after the container has been created and settled (i.e. not in the same
// deployment that first creates it).

@description('Name of the storage account that holds the token store container')
param storageAccountName string

@description('Name of the blob container used as the Easy Auth token store')
param containerName string

@description('Expiry timestamp for the SAS URL (e.g. 2036-01-01T00:00:00Z)')
param sasExpiry string

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' existing = {
  parent: storageAccount
  name: 'default'
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' existing = {
  parent: blobService
  name: containerName
}

var sasToken = container.listServiceSas('2023-01-01', {
  canonicalizedResource: '/blob/${storageAccountName}/${containerName}'
  signedResource: 'c'
  signedPermission: 'racwdl'
  signedProtocol: 'https'
  signedExpiry: sasExpiry
}).serviceSasToken

output sasUrl string = 'https://${storageAccountName}.blob.${environment().suffixes.storage}/${containerName}?${sasToken}'
