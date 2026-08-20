using './main.bicep'

// Override defaults as needed.

param location = 'westeurope'
param nameSuffix = 'stowhaven-weu-dev'
param nameSuffixStr = 'stowhavenweudev'
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
// Authentication values are injected at deploy time by the GitHub workflow.
// Container Apps are skipped unless Gateway and API auth are supplied.
param gatewayAuthClientId = ''
param gatewayAuthClientSecret = ''
param gatewayAuthAllowedAudiences = []
param cosmosAccountName = 'cosmos-stowhaven-weu-dev'
param cosmosDatabaseName = 'backup-state'
param cosmosDatabaseAutoscaleMaxThroughput = 1000
param cosmosManifestContainerName = 'manifest-state'
param cosmosDeviceRegistryContainerName = 'device-registry'
param daprAzureClientId = ''
param keyVaultNetworkDefaultAction = 'Allow'
