#!/usr/bin/env pwsh
<#
.SYNOPSIS
Creates the Entra ID app registration for the Stowhaven API.

.DESCRIPTION
Creates an API app registration, sets the Application ID URI to api://{appId},
and exposes the delegated scopes used by client applications:

- backup.client: normal backup/restore clients
- backup.admin: trusted administrative/operator clients

It also exposes the application role used by the Swagger Gateway managed identity:

- backup.gateway: Gateway application access to the Stowhaven API

After creating this API app, set the GitHub repository variable API_AUTH_CLIENT_ID
to the printed app ID and redeploy the Container Apps stack.

.EXAMPLE
./scripts/New-BackupApiAppRegistration.ps1

.EXAMPLE
./scripts/New-BackupApiAppRegistration.ps1 -DisplayName "Stowhaven API"
#>

[CmdletBinding()]
param(
    [string]$DisplayName = "Stowhaven API",

    [string]$TenantId
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

Write-Host "Using tenant: $TenantId"
Write-Host "Looking up API app registration: $DisplayName"

$existingApps = @(Invoke-AzCliJson -Arguments @(
    "ad", "app", "list",
    "--display-name", $DisplayName
))

# Reuse the pre-branding registration when this script is rerun against an
# existing deployment. Display names are not token identities, so renaming it
# in the Entra portal remains optional.
if ($existingApps.Count -eq 0 -and $DisplayName -eq "Stowhaven API") {
    $existingApps = @(Invoke-AzCliJson -Arguments @(
        "ad", "app", "list",
        "--display-name", "Backup API"
    ))

    if ($existingApps.Count -eq 1) {
        Write-Host "Reusing legacy 'Backup API' app registration for Stowhaven."
    }
}

if ($existingApps.Count -gt 1) {
    throw "Found multiple app registrations named '$DisplayName'. Rename duplicates or use a unique display name before running this script again."
}

if ($existingApps.Count -eq 1) {
    $apiApp = $existingApps[0]
    Write-Host "Reusing existing API app registration: $($apiApp.appId)"
}
else {
    Write-Host "Creating API app registration: $DisplayName"
    $apiApp = Invoke-AzCliJson -Arguments @(
        "ad", "app", "create",
        "--display-name", $DisplayName,
        "--sign-in-audience", "AzureADMyOrg"
    )
}

$apiAppId = $apiApp.appId
$identifierUri = "api://$apiAppId"

if (@($apiApp.identifierUris) -notcontains $identifierUri) {
    Write-Host "Setting Application ID URI: $identifierUri"
    Invoke-AzCli -Arguments @(
        "ad", "app", "update",
        "--id", $apiAppId,
        "--identifier-uris", $identifierUri
    )
}
else {
    Write-Host "Application ID URI already set: $identifierUri"
}

$apiApp = Invoke-AzCliJson -Arguments @("ad", "app", "show", "--id", $apiAppId)
$existingScopes = @($apiApp.api.oauth2PermissionScopes)
$backupClientScope = $existingScopes | Where-Object { $_.value -eq "backup.client" } | Select-Object -First 1
$backupAdminScope = $existingScopes | Where-Object { $_.value -eq "backup.admin" } | Select-Object -First 1
$backupClientScopeId = if ($backupClientScope) { $backupClientScope.id } else { [guid]::NewGuid().ToString() }
$backupAdminScopeId = if ($backupAdminScope) { $backupAdminScope.id } else { [guid]::NewGuid().ToString() }

$existingAppRoles = @($apiApp.appRoles)
$backupGatewayAppRole = $existingAppRoles | Where-Object { $_.value -eq "backup.gateway" } | Select-Object -First 1
$backupGatewayAppRoleId = if ($backupGatewayAppRole) { $backupGatewayAppRole.id } else { [guid]::NewGuid().ToString() }
$appRoles = @($existingAppRoles | Where-Object { $_.value -ne "backup.gateway" }) + @(
    @{
        allowedMemberTypes = @("Application")
        description = "Allows the Stowhaven Gateway managed identity to call the Stowhaven API."
        displayName = "Stowhaven Gateway"
        id = $backupGatewayAppRoleId
        isEnabled = $true
        value = "backup.gateway"
    }
)

$apiDefinition = @{
    appRoles = $appRoles
    api = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes = @(
            @{
                adminConsentDescription = "Allows a backup client to register devices, start backup runs, commit runs, and restore its own data."
                adminConsentDisplayName = "Use Stowhaven as a backup client"
                id = $backupClientScopeId
                isEnabled = $true
                type = "User"
                userConsentDescription = "Back up and restore your data using Stowhaven."
                userConsentDisplayName = "Back up and restore your data"
                value = "backup.client"
            },
            @{
                adminConsentDescription = "Allows trusted operators to use administrative Stowhaven operations."
                adminConsentDisplayName = "Administer Stowhaven"
                id = $backupAdminScopeId
                isEnabled = $true
                type = "Admin"
                userConsentDescription = "Administer Stowhaven."
                userConsentDisplayName = "Administer Stowhaven"
                value = "backup.admin"
            }
        )
    }
} | ConvertTo-Json -Depth 10 -Compress

$temporaryApiFile = New-TemporaryFile
try {
    Set-Content -Path $temporaryApiFile -Value $apiDefinition -NoNewline

    Write-Host "Exposing delegated scopes: backup.client, backup.admin"
    Write-Host "Exposing application role: backup.gateway"
    Invoke-AzCli -Arguments @(
        "rest",
        "--method", "PATCH",
        "--uri", "https://graph.microsoft.com/v1.0/applications(appId='$apiAppId')",
        "--headers", "Content-Type=application/json",
        "--body", "@$temporaryApiFile"
    )
}
finally {
    Remove-Item -Path $temporaryApiFile -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Stowhaven API app registration created." -ForegroundColor Green
Write-Host ""
Write-Host "API app ID:"
Write-Host "  $apiAppId"
Write-Host ""
Write-Host "Application ID URI / audience:"
Write-Host "  $identifierUri"
Write-Host ""
Write-Host "Application role for Gateway managed identity:"
Write-Host "  backup.gateway"
Write-Host ""
Write-Host "Set these GitHub repository variables before redeploying:"
Write-Host "  API_AUTH_CLIENT_ID = $apiAppId"
Write-Host "  API_AUTH_AUDIENCE  = $identifierUri"
Write-Host ""
Write-Host "Then create client app registrations with:"
Write-Host "  ./scripts/New-BackupClientAppRegistration.ps1 -ApiAppId $apiAppId -DisplayName 'Stowhaven Client' -GrantAdminConsent"
Write-Host "  ./scripts/New-BackupClientAppRegistration.ps1 -ApiAppId $apiAppId -DisplayName 'Stowhaven Admin Client' -IncludeAdminScope -GrantAdminConsent"
Write-Host ""
