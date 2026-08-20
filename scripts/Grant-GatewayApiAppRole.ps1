#!/usr/bin/env pwsh
<#
.SYNOPSIS
Grants the Stowhaven API gateway application role to the Gateway managed identity.

.DESCRIPTION
Assigns the `backup.gateway` application role from the Stowhaven API app registration
to the Gateway Container App managed identity service principal. Run this after
the Gateway Container App has a system-assigned managed identity.

.EXAMPLE
./scripts/Grant-GatewayApiAppRole.ps1 -ApiAppId "<api-app-id>" -GatewayPrincipalId "<gateway-managed-identity-object-id>"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiAppId,

    [Parameter(Mandatory = $true)]
    [string]$GatewayPrincipalId,

    [string]$AppRoleValue = "backup.gateway"
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

$apiApp = Invoke-AzCliJson -Arguments @("ad", "app", "show", "--id", $ApiAppId)
$apiServicePrincipal = Invoke-AzCliJson -Arguments @("ad", "sp", "show", "--id", $ApiAppId)

$appRole = @($apiApp.appRoles) | Where-Object { $_.value -eq $AppRoleValue -and $_.isEnabled } | Select-Object -First 1
if (-not $appRole) {
    throw "Could not find enabled app role '$AppRoleValue' on API app '$ApiAppId'. Run New-BackupApiAppRegistration.ps1 first."
}

$existingAssignments = Invoke-AzCliJson -Arguments @(
    "rest",
    "--method", "GET",
    "--uri", "https://graph.microsoft.com/v1.0/servicePrincipals/$GatewayPrincipalId/appRoleAssignments"
)

$alreadyAssigned = @($existingAssignments.value) | Where-Object {
    $_.resourceId -eq $apiServicePrincipal.id -and $_.appRoleId -eq $appRole.id
} | Select-Object -First 1

if ($alreadyAssigned) {
    Write-Host "Gateway managed identity already has '$AppRoleValue'." -ForegroundColor Green
    return
}

$body = @{
    principalId = $GatewayPrincipalId
    resourceId = $apiServicePrincipal.id
    appRoleId = $appRole.id
} | ConvertTo-Json -Compress

$temporaryBodyFile = New-TemporaryFile
try {
    Set-Content -Path $temporaryBodyFile -Value $body -NoNewline

    Invoke-AzCli -Arguments @(
        "rest",
        "--method", "POST",
        "--uri", "https://graph.microsoft.com/v1.0/servicePrincipals/$GatewayPrincipalId/appRoleAssignments",
        "--headers", "Content-Type=application/json",
        "--body", "@$temporaryBodyFile"
    )
}
finally {
    Remove-Item -Path $temporaryBodyFile -Force -ErrorAction SilentlyContinue
}

Write-Host "Granted '$AppRoleValue' to Gateway managed identity '$GatewayPrincipalId'." -ForegroundColor Green
