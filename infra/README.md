# Backup API Infrastructure

This directory contains the Terraform configuration for the Backup API infrastructure on Azure.

## File Structure

The Terraform configuration is organized into the following files:

- **`main.tf`** - Main entry point with documentation about the structure
- **`versions.tf`** - Terraform and provider version requirements and backend configuration
- **`variables.tf`** - Input variable definitions for configuration
- **`locals.tf`** - Local value definitions (common tags, computed values)
- **`data.tf`** - Data source definitions for external resources
- **`storage.tf`** - Storage accounts, containers, and lifecycle policies
- **`monitoring.tf`** - Log Analytics workspace and Application Insights
- **`function_app.tf`** - Function App and App Service Plan resources
- **`iam.tf`** - Role assignments and IAM permissions
- **`outputs.tf`** - Output value definitions

## Resources Created

### Storage
- Data storage account for backups (Cool tier)
- Function storage account for Azure Functions runtime
- Backup container with private access
- Lifecycle management policy (archive after 30 days)

### Compute
- Linux Function App with .NET 8.0 runtime
- Consumption App Service Plan (Y1 SKU)

### Monitoring
- Log Analytics workspace with configurable retention
- Application Insights for telemetry

### Security
- System-assigned managed identity for Function App
- Storage Blob Data Contributor role assignment

## Usage

1. Initialize Terraform:
   ```bash
   terraform init
   ```

2. Plan the deployment:
   ```bash
   terraform plan
   ```

3. Apply the configuration:
   ```bash
   terraform apply
   ```

## Variables

Key variables that can be customized:

- `location` - Azure region (default: westeurope)
- `name_suffix` - Resource name suffix (default: fdev-weu-prd)
- `api_key` - API key for authentication (sensitive)
- `lifecycle_archive_after_days` - Days before archiving (default: 30)
- `log_analytics_retention_days` - Log retention period (default: 30)

## Outputs

The configuration provides outputs for:
- Function App name and URL
- Storage account names
- Monitoring resource details
- Application Insights instrumentation key
