using './main.bicep'

// Override defaults as needed.

param location = 'northeurope'
param nameSuffix = 'fdev-neu-prd'
param nameSuffixStr = 'fdevneuprd'
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
param daprAzureClientId = ''
param keyVaultNetworkDefaultAction = 'Allow'
