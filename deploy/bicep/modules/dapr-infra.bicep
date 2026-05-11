// Dapr infrastructure: Key Vault
// Note: Key Vault secrets are created in main.bicep after IAM role assignments are in place.

@description('Azure region for resources')
param location string

@description('Pre-determined Key Vault name (passed from main.bicep to avoid circular deps)')
param keyVaultName string

@description('Azure AD tenant ID for Key Vault')
param tenantId string

@description('Key Vault network ACL default action. Allow is required unless private endpoint/VNet routing for Container Apps/Dapr has been configured.')
@allowed([
  'Allow'
  'Deny'
])
param keyVaultNetworkDefaultAction string = 'Allow'

@description('Common resource tags')
param tags object

// ---------------------------------------------------------------------------
// Key Vault (Dapr Secret Store)
// Name is passed in from main.bicep so compute.bicep can reference it without
// creating a circular dependency.
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: keyVaultNetworkDefaultAction
    }
  }
  tags: tags
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output keyVaultName string = keyVault.name
output keyVaultId string = keyVault.id
