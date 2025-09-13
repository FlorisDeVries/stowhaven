# PowerShell script to stop local development environment

Write-Host "Stopping local development environment..." -ForegroundColor Yellow

# Stop DAPR and application (if running)
Write-Host "Stopping DAPR processes..." -ForegroundColor Yellow
dapr stop --app-id backup-api

# Stop Docker services
Write-Host "Stopping Redis and Azurite..." -ForegroundColor Yellow
docker-compose -f docker-compose.local.yml down

Write-Host "Development environment stopped." -ForegroundColor Green
