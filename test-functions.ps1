# Test script for Azure Functions (ASCII-safe)

Write-Host "Testing Azure Functions..." -ForegroundColor Green

# Function App details
$functionAppName = "func-fdev-weu-prd"
$resourceGroup   = "rg-fdev-weu-backup-prd"

# Check if logged into Azure
try {
    $account = az account show --query "user.name" -o tsv 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Not logged into Azure. Please run 'az login' first." -ForegroundColor Red
        exit 1
    }
    Write-Host ("Logged into Azure as: {0}" -f $account) -ForegroundColor Green
}
catch {
    Write-Host "Azure CLI not available or not logged in. Please install Azure CLI and run 'az login'." -ForegroundColor Red
    exit 1
}

# Get Function App URL
Write-Host ""
Write-Host "Getting Function App URL..." -ForegroundColor Cyan
try {
    $functionUrl = az functionapp show --name $functionAppName --resource-group $resourceGroup --query "defaultHostName" -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionUrl)) {
        Write-Host "Failed to get Function App URL. Make sure the Function App exists." -ForegroundColor Red
        exit 1
    }
    $baseUrl = "https://$functionUrl"
    Write-Host ("Function App URL: {0}" -f $baseUrl) -ForegroundColor Green
}
catch {
    Write-Host ("Error getting Function App URL: {0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 1
}

# Test 1: Health endpoint (no authentication required)
Write-Host ""
Write-Host "Testing Health endpoint..." -ForegroundColor Cyan
try {
    $healthUrl = "$baseUrl/api/health"
    Write-Host ("Calling: {0}" -f $healthUrl)
    $response = Invoke-RestMethod -Uri $healthUrl -Method GET
    Write-Host "Health endpoint working!" -ForegroundColor Green
    Write-Host "Response:" -ForegroundColor Yellow
    $json = $response | ConvertTo-Json -Depth 5
    Write-Host $json
}
catch {
    Write-Host ("Health endpoint failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    if ($_.Exception -and $_.Exception.Response) {
        try {
            $status = $_.Exception.Response.StatusCode
            if ($status -and $status.PSObject.Properties['Value__']) { $status = $status.Value__ }
            Write-Host ("Status Code: {0}" -f $status) -ForegroundColor Red
        } catch { }
    }
}

# Test 2: Get Function Keys for authenticated endpoints
Write-Host ""
Write-Host "Getting Function Keys..." -ForegroundColor Cyan
try {
    $keys = az functionapp keys list --name $functionAppName --resource-group $resourceGroup --query "functionKeys" -o json | ConvertFrom-Json
    if (-not $keys -or $keys.PSObject.Properties.Count -eq 0) {
        Write-Host "No function keys found. Getting host keys instead..." -ForegroundColor Yellow
        $hostKeys = az functionapp keys list --name $functionAppName --resource-group $resourceGroup --query "systemKeys" -o json | ConvertFrom-Json
        if ($hostKeys -and $hostKeys.PSObject.Properties.Count -gt 0) {
            $functionKey = ($hostKeys.PSObject.Properties | Select-Object -First 1).Value
            Write-Host "Using host key for testing" -ForegroundColor Green
        }
        else {
            Write-Host "No keys available for testing authenticated endpoints" -ForegroundColor Red
            Write-Host "You can still test the health endpoint above" -ForegroundColor Yellow
            exit 0
        }
    }
    else {
        $functionKey = ($keys.PSObject.Properties | Select-Object -First 1).Value
        Write-Host "Got function key for testing" -ForegroundColor Green
    }
}
catch {
    Write-Host ("Failed to get function keys: {0}" -f $_.Exception.Message) -ForegroundColor Red
    Write-Host "You can still test the health endpoint above" -ForegroundColor Yellow
    exit 0
}

# Test 3: SAS Upload endpoint (requires API key)
Write-Host ""
Write-Host "Testing SAS Upload endpoint..." -ForegroundColor Cyan
try {
    $sasUrl = "$baseUrl/api/get-sas-upload?code=$functionKey"
    Write-Host ("Calling: {0}" -f $sasUrl)

    # Sample request body
    $requestBody = @{
        fileName        = "test-backup.zip"
        expirationHours = 1
    } | ConvertTo-Json

    Write-Host ("Request body: {0}" -f $requestBody) -ForegroundColor Yellow

    # Replace X-API-Key with your real key to fully test
    $headers = @{
        "Content-Type" = "application/json"
        "X-API-Key"    = "your-api-key-here"
    }

    $response = Invoke-RestMethod -Uri $sasUrl -Method POST -Body $requestBody -Headers $headers
    Write-Host "SAS Upload endpoint working!" -ForegroundColor Green
    Write-Host "Response:" -ForegroundColor Yellow
    $json = $response | ConvertTo-Json -Depth 5
    Write-Host $json
}
catch {
    $status = $null
    if ($_.Exception -and $_.Exception.Response) {
        try {
            $status = $_.Exception.Response.StatusCode
            if ($status -and $status.PSObject.Properties['Value__']) { $status = $status.Value__ }
        } catch { }
    }

    if ($status -eq 401) {
        Write-Host "SAS Upload endpoint is accessible but requires a valid API key" -ForegroundColor Yellow
        Write-Host "Update the X-API-Key header in this script with your actual API key to test fully" -ForegroundColor Yellow
    }
    else {
        Write-Host ("SAS Upload endpoint failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
        if ($status) { Write-Host ("Status Code: {0}" -f $status) -ForegroundColor Red }
    }
}

Write-Host ""
Write-Host "Function testing complete!" -ForegroundColor Green
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host '  1. If health endpoint works, your Function App is running correctly' -ForegroundColor White
Write-Host '  2. For SAS endpoints, update the API key in this script' -ForegroundColor White
Write-Host '  3. Check Azure Portal > Function App > Log Stream for detailed logs' -ForegroundColor White
Write-Host ('  4. Use the health endpoint URL for monitoring: {0}/api/health' -f $baseUrl) -ForegroundColor White
