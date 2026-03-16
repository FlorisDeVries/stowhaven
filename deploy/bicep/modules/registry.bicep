// Azure Container Registry

@description('Azure region for resources')
param location string

@description('Suffix for storage account-style names (no dashes)')
@minLength(2)
param nameSuffixStr string

@description('Common resource tags')
param tags object

// ---------------------------------------------------------------------------
// Container Registry
// ---------------------------------------------------------------------------

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'acr${nameSuffixStr}'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
  tags: tags
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output name string = containerRegistry.name
output loginServer string = containerRegistry.properties.loginServer
output id string = containerRegistry.id
