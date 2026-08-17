#!/usr/bin/env pwsh
<#
.SYNOPSIS
Creates an Entra ID public/native app registration for the Stowhaven Client.

.DESCRIPTION
Creates a desktop/public client app registration, configures http://localhost as
the native redirect URI, enables public client flows, and grants delegated
permissions to the existing Stowhaven API app registration.

By default, only the normal backup client scope (`backup.client`) is granted.
Use -IncludeAdminScope only for trusted operator/admin clients.

.EXAMPLE
./scripts/New-BackupClientAppRegistration.ps1

.EXAMPLE
./scripts/New-BackupClientAppRegistration.ps1 -DisplayName "Stowhaven Client - Floris Laptop" -GrantAdminConsent

.EXAMPLE
./scripts/New-BackupClientAppRegistration.ps1 -IncludeAdminScope -GrantAdminConsent
#>

[CmdletBinding()]
param(
    [string]$DisplayName = "Stowhaven Client",

    [Parameter(Mandatory = $true)]
    [string]$ApiAppId,

    [string]$TenantId,

    [string]$ApiUrl = "https://ca-fdev-weu-prd.kinddesert-f7d01f23.westeurope.azurecontainerapps.io",

    [switch]$IncludeAdminScope,

    [switch]$GrantAdminConsent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-AzCliJson {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $json = & az @Arguments --output json
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments -join ' ')"
    }

    if ([string]::IsNullOrWhiteSpace($json)) {
        return $null
    }

    return $json | ConvertFrom-Json
}

function Invoke-AzCli {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & az @Arguments --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments -join ' ')"
    }
}

function Ensure-ServicePrincipal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppId
    )

    $existingServicePrincipal = & az ad sp show --id $AppId --output json 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingServicePrincipal)) {
        return
    }

    Invoke-AzCli -Arguments @(
        "ad", "sp", "create",
        "--id", $AppId
    )
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI is required. Install it first: https://learn.microsoft.com/cli/azure/install-azure-cli"
}

$account = Invoke-AzCliJson -Arguments @("account", "show")
if (-not $account) {
    throw "Azure CLI is not logged in. Run: az login"
}

if ([string]::IsNullOrWhiteSpace($TenantId)) {
    $TenantId = $account.tenantId
}

$requestedScopes = [System.Collections.Generic.List[string]]::new()
$requestedScopes.Add("backup.client")

if ($IncludeAdminScope) {
    $requestedScopes.Add("backup.admin")
}

Write-Host "Using tenant: $TenantId"
Write-Host "Using API app: $ApiAppId"
Write-Host "Requested scopes: $($requestedScopes -join ', ')"

$apiApp = Invoke-AzCliJson -Arguments @("ad", "app", "show", "--id", $ApiAppId)
if (-not $apiApp) {
    throw "Could not find API app registration '$ApiAppId'."
}

$apiScopes = @($apiApp.api.oauth2PermissionScopes)
$apiPermissions = [System.Collections.Generic.List[string]]::new()

foreach ($scope in $requestedScopes) {
    $scopeDefinition = $apiScopes | Where-Object { $_.value -eq $scope } | Select-Object -First 1

    if (-not $scopeDefinition) {
        throw "Could not find delegated scope '$scope' on API app '$ApiAppId'. Create it on the API app registration first under 'Expose an API'."
    }

    $apiPermissions.Add("$($scopeDefinition.id)=Scope")
}

Write-Host "Creating public/native client app registration: $DisplayName"

$clientApp = Invoke-AzCliJson -Arguments @(
    "ad", "app", "create",
    "--display-name", $DisplayName,
    "--sign-in-audience", "AzureADMyOrg",
    "--public-client-redirect-uris", "http://localhost",
    "--enable-access-token-issuance", "false",
    "--enable-id-token-issuance", "false"
)

$clientAppId = $clientApp.appId

Write-Host "Enabling public client flow"
Invoke-AzCli -Arguments @(
    "ad", "app", "update",
    "--id", $clientAppId,
    "--set", "isFallbackPublicClient=true"
)

Write-Host "Adding delegated API permission(s): $($requestedScopes -join ', ')"
$permissionAddArguments = @(
    "ad", "app", "permission", "add",
    "--id", $clientAppId,
    "--api", $ApiAppId,
    "--api-permissions"
) + $apiPermissions.ToArray()

Invoke-AzCli -Arguments $permissionAddArguments

if ($GrantAdminConsent) {
    Write-Host "Creating service principals required for delegated permission consent"
    Ensure-ServicePrincipal -AppId $ApiAppId
    Ensure-ServicePrincipal -AppId $clientAppId

    Write-Host "Granting tenant-wide delegated consent. This requires sufficient directory privileges."
    Invoke-AzCli -Arguments @(
        "ad", "app", "permission", "grant",
        "--id", $clientAppId,
        "--api", $ApiAppId,
        "--scope", ($requestedScopes -join " ")
    )
}
else {
    Write-Host "Skipping admin consent. The first interactive sign-in may show a consent prompt."
}

$scopeForConfig = if ($IncludeAdminScope) { "backup.admin" } else { "backup.client" }

Write-Host ""
Write-Host "Stowhaven Client app registration created." -ForegroundColor Green
Write-Host ""
Write-Host "Client app ID:"
Write-Host "  $clientAppId"
Write-Host ""
Write-Host "Use these values in src/services/client/appsettings.json:"
Write-Host ""
Write-Host @"
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "$TenantId",
    "ClientId": "$clientAppId"
  },
  "BackupApiClient": {
    "ApiUrl": "$ApiUrl",
    "AuthenticationScope": "api://$ApiAppId/$scopeForConfig",
    "AuthenticationTenant": "$TenantId"
  }
"@
Write-Host ""
Write-Host "Portal checks:"
Write-Host "  - Authentication > Mobile and desktop applications includes http://localhost"
Write-Host "  - Authentication > Allow public client flows is Yes"
Write-Host "  - API permissions includes delegated permission(s): $($requestedScopes -join ', ')"
Write-Host ""
Write-Host "Recommended portal follow-up:"
Write-Host "  - Open the matching Enterprise Application"
Write-Host "  - Set Properties > Assignment required? to Yes"
Write-Host "  - Assign users/groups manually under Users and groups"
Write-Host ""
