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
param apiMinReplicas = 1
param cosmosAccountName = 'cosmos-fdev-weu-prd'
param cosmosDatabaseName = 'backup-state'
param cosmosDatabaseThroughput = 400
param cosmosManifestContainerName = 'manifest-state'
param cosmosDeviceRegistryContainerName = 'device-registry'
param daprAzureClientId = ''
param keyVaultNetworkDefaultAction = 'Allow'
