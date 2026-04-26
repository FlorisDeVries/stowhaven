// Dapr infrastructure: Redis Cache, Service Bus, and Key Vault
// Note: Key Vault secrets are created in main.bicep after IAM role assignments are in place.

@description('Azure region for resources')
param location string

@description('Suffix for resource names (with dashes)')
param nameSuffix string

@description('Pre-determined Key Vault name (passed from main.bicep to avoid circular deps)')
param keyVaultName string

@description('Azure AD tenant ID for Key Vault')
param tenantId string

@description('Common resource tags')
param tags object

// ---------------------------------------------------------------------------
// Redis Cache (Dapr State Store)
// ---------------------------------------------------------------------------

resource redisCache 'Microsoft.Cache/redis@2023-08-01' = {
  name: 'redis-${nameSuffix}'
  location: location
  properties: {
    sku: {
      name: 'Standard'
      family: 'C'
      capacity: 1
    }
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
  }
  tags: tags
}

// ---------------------------------------------------------------------------
// Service Bus (Dapr Pub/Sub)
// ---------------------------------------------------------------------------

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: 'sb-${nameSuffix}'
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  tags: tags
}

resource backupEventsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'backup-events'
  properties: {
    enablePartitioning: true
  }
}

resource backupWorkerSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: backupEventsTopic
  name: 'backup-worker'
  properties: {
    maxDeliveryCount: 3
  }
}

resource backupWorkerScaleListenRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'backup-worker-scale-listen'
  properties: {
    rights: [
      'Listen'
    ]
  }
}

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
      defaultAction: 'Allow' // Tighten to 'Deny' with explicit rules in production
    }
  }
  tags: tags
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output keyVaultName string = keyVault.name
output keyVaultId string = keyVault.id
output redisCacheName string = redisCache.name
output serviceBusNamespaceName string = serviceBusNamespace.name
@secure()
output serviceBusScaleConnectionString string = backupWorkerScaleListenRule.listKeys().primaryConnectionString
