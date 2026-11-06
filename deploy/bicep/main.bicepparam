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
// The account's 4000 RU/s totalThroughputLimit counts this ceiling once per region, so with
// 2 regions 2000 is the most it allows (3000 was rejected as 6000 RU/s). The 200 RU/s idle floor
// (400 total) stays inside the free tier's 1000 RU/s; only hours a commit scales up are billed.
param cosmosDatabaseAutoscaleMaxThroughput = 2000
param cosmosManifestContainerName = 'manifest-state'
param cosmosDeviceRegistryContainerName = 'device-registry'
param daprAzureClientId = ''
param keyVaultNetworkDefaultAction = 'Allow'
