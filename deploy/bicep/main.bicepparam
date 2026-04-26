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
param daprAzureClientId = ''
param keyVaultNetworkDefaultAction = 'Allow'
