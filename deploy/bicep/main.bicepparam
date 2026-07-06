using './main.bicep'

// Override defaults as needed.

param location = 'westeurope'
param nameSuffix = 'fdev-weu-prd'
param nameSuffixStr = 'fdevweuprd'
param lifecycleArchiveAfterDays = 30
param logAnalyticsRetentionDays = 30
param logAnalyticsDailyQuotaGb = 1
param imageTag = 'latest'
param allowCopyDeleteFallback = false
param enableSasIpRestriction = false
param staleStagingCleanupCronSchedule = '@every 24h'
param staleStagingCleanupOlderThanHours = 24
param staleStagingCleanupMaxDeletes = 500
param staleStagingCleanupDryRun = false
param apiMinReplicas = 0
param gatewayMinReplicas = 0
// Gateway auth values are injected at deploy time by the GitHub workflow from
// repo vars/secrets (GATEWAY_AUTH_CLIENT_ID / GATEWAY_AUTH_CLIENT_SECRET) —
// the empty values below are placeholders, not the deployed configuration.
// WARNING: deploying this file directly (e.g. via `az deployment group create`
// without those overrides) disables Easy Auth AND the OBO exchange on the
// Gateway, silently falling back to its managed-identity token.
param gatewayAuthClientId = ''
param gatewayAuthClientSecret = ''
param gatewayAuthAllowedAudiences = []
param cosmosAccountName = 'cosmos-fdev-weu-prd'
param cosmosDatabaseName = 'backup-state'
param cosmosDatabaseAutoscaleMaxThroughput = 1000
param cosmosManifestContainerName = 'manifest-state'
param cosmosDeviceRegistryContainerName = 'device-registry'
param daprAzureClientId = ''
param keyVaultNetworkDefaultAction = 'Allow'
param apiAuthTenantId = 'cf8adfe1-bb3b-4ef0-8ba9-44dcddb8ecb9'
param apiAuthClientId = '906eb0e3-e351-47c0-a68a-690207f4cccb'
param apiAuthAudience = 'api://906eb0e3-e351-47c0-a68a-690207f4cccb'
