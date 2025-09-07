@echo off
echo This script requires PowerShell. Running setup-github-secrets.ps1...
echo.

if "%~1"=="" (
    echo Usage: setup-github-secrets.cmd [GitHubRepo] [SubscriptionId] [AppName]
    echo Example: setup-github-secrets.cmd FlorisDeVries/backup-api
    echo.
    pause
    exit /b 1
)

powershell.exe -ExecutionPolicy Bypass -File "%~dp0setup-github-secrets.ps1" %*
