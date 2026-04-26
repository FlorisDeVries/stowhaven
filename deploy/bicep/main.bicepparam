using './main.bicep'

// Override defaults as needed. Sensitive params (apiKey) are supplied via
// --parameters apiKey=$API_KEY in CI or interactively via az CLI prompt.

param location = 'northeurope'
param nameSuffix = 'fdev-neu-prd'
param nameSuffixStr = 'fdevneuprd'
param lifecycleArchiveAfterDays = 30
param logAnalyticsRetentionDays = 30
param logAnalyticsDailyQuotaGb = 1
param imageTag = 'latest'
param allowCopyDeleteFallback = false
param apiKey = ''

// apiKey is @secure() – pass via CI secret or CLI:
//   az deployment group create ... --parameters apiKey=${{ secrets.API_KEY }}
