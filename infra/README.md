# Backup API Infrastructure

This directory contains the Terraform configuration for the Backup API infrastructure on Azure, now migrated to **Azure Container Apps** for better scalability and container support.

## Architecture Overview

The application has been migrated from Azure Functions to Azure Container Apps (ACA) for the following benefits:
- **Container-native deployment** with Docker support
- **Better scaling control** with min/max replicas
- **Cost optimization** with scale-to-zero capability
- **Standard HTTP endpoints** instead of Function triggers
- **Simplified development** with ASP.NET Core Web API

## File Structure

The Terraform configuration is organized into the following files:

- **`main.tf`** - Main entry point with documentation about the structure
- **`versions.tf`** - Terraform and provider version requirements and backend configuration
- **`variables.tf`** - Input variable definitions for configuration
- **`locals.tf`** - Local value definitions (common tags, computed values)
- **`data.tf`** - Data source definitions for external resources
- **`storage.tf`** - Storage accounts, containers, and lifecycle policies
- **`monitoring.tf`** - Log Analytics workspace and Application Insights
- **`container_registry.tf`** - Azure Container Registry for Docker images
- **`container_app.tf`** - Container App Environment and Container App
- **`iam.tf`** - Role assignments and IAM permissions
- **`outputs.tf`** - Output value definitions
- **`function_app_deprecated.tf`** - (Deprecated) Original Function App config for reference

## Resources Created

### Container Platform
- Azure Container Registry (ACR) for storing Docker images
- Container App Environment with Log Analytics integration
- Container App with auto-scaling (0-10 replicas)

### Storage
- Data storage account for backups (Cool tier)
- ~~Function storage account~~ (removed - not needed for Container Apps)
- Backup container with private access
- Lifecycle management policy (archive after 30 days)

### Monitoring
- Log Analytics workspace with configurable retention
- Application Insights for telemetry and distributed tracing

### Security
- System-assigned managed identity for Container App
- Storage Blob Data Contributor role assignment
- ACR Pull permissions for image deployment

## Application Structure

The application has been migrated to ASP.NET Core Web API:
- **`src/`** - Main application source code
- **`src/Controllers/`** - Web API controllers (SAS, Health)
- **`src/Services/`** - Business logic and Azure Storage integration
- **`src/Models/`** - Request/response models
- **`Dockerfile`** - Container build configuration

## API Endpoints

- `GET /health` - Health check endpoint (no authentication required)
- `POST /api/get-sas-upload` - Generate SAS URL for file upload
- `POST /api/get-sas-download` - Generate SAS URL for file download

Authentication: `X-API-Key` header required for SAS endpoints.

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
- Container App name and URL
- Container Registry details
- Storage account names
- Monitoring resource details

## Migration Notes

- Function App resources have been commented out in `function_app_deprecated.tf`
- The Function storage account can be removed after successful migration
- Container Apps use managed identity for Azure Storage access
- Application Insights connection is maintained for telemetry
