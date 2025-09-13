# PowerShell script to start local development environment

Write-Host "Starting local development environment..." -ForegroundColor Green

# Start Redis and Azurite
Write-Host "Starting Redis and Azurite..." -ForegroundColor Yellow
docker-compose -f docker-compose.local.yml up -d

# Wait for services to start
Start-Sleep -Seconds 5

# Start the application with DAPR
Write-Host "Starting backup API with DAPR..." -ForegroundColor Yellow
dapr run --app-id backup-api --app-port 8080 --dapr-http-port 3500 --dapr-grpc-port 50001 --config ./dapr/components/config.yaml --resources-path ./dapr/components/local -- dotnet run --project ./src/BackupApi.csproj

Write-Host "Development environment is ready!" -ForegroundColor Green
Write-Host "API available at: http://localhost:8080" -ForegroundColor Cyan
Write-Host "DAPR dashboard: http://localhost:8080" -ForegroundColor Cyan
Write-Host "Redis: localhost:6379" -ForegroundColor Cyan
Write-Host "Azurite: localhost:10000" -ForegroundColor Cyan
