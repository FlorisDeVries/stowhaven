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
// 3000 keeps large commits from saturating the database while staying below the account's
// 4000 RU/s guardrail. Each region bills separately: with 2 regions the 300 RU/s idle floor
// (600 total) stays inside the free tier's 1000 RU/s; only hours a commit scales up are billed.
param cosmosDatabaseAutoscaleMaxThroughput = 3000
param cosmosManifestContainerName = 'manifest-state'
param cosmosDeviceRegistryContainerName = 'device-registry'
param daprAzureClientId = ''
param keyVaultNetworkDefaultAction = 'Allow'
