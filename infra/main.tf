terraform {
  required_version = ">= 1.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.100"
    }
  }
  
  backend "azurerm" {
    resource_group_name  = "rg-fdev-weu-backup-prd"
    storage_account_name = "staterraformfdevweuprd"
    container_name       = "tfstate"
    key                  = "backup-api.tfstate"
  }
}

provider "azurerm" {
  features {}
}

variable "location" {
  description = "Azure region for resources"
  type        = string
  default     = "westeurope"
}

variable "name_suffix" {
  description = "Suffix for resource names"
  type        = string
  default     = "fdev-weu-prd"
}

variable "name_suffix_str" {
  description = "Suffix for storage account names (no dashes)"
  type        = string
  default     = "fdevweuprd"
}

variable "lifecycle_archive_after_days" {
  description = "Days after which blobs are moved to archive tier"
  type        = number
  default     = 30
}

variable "api_key" {
  description = "API key for authenticating requests to the backup API"
  type        = string
  sensitive   = true
}

variable "log_analytics_retention_days" {
  description = "Retention period for Log Analytics workspace in days"
  type        = number
  default     = 30
}

variable "log_analytics_daily_quota_gb" {
  description = "Daily ingestion quota for Log Analytics workspace in GB"
  type        = number
  default     = 1
}

# Data storage account
resource "azurerm_storage_account" "data" {
  name                     = "stabackup${var.name_suffix_str}"
  resource_group_name      = data.azurerm_resource_group.main.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Cool"
  
  allow_nested_items_to_be_public = false
  min_tls_version                 = "TLS1_2"

  tags = {
    Environment = "Production"
    Project     = "BackupAPI"
  }
}

# Container for backups
resource "azurerm_storage_container" "backups" {
  name                  = "backups"
  # Note: storage_account_name is marked as deprecated but storage_account_id is not yet supported
  # Keep using storage_account_name until the provider fully supports the new property
  storage_account_name  = azurerm_storage_account.data.name
  container_access_type = "private"
}

# Lifecycle management policy
resource "azurerm_storage_management_policy" "lifecycle" {
  storage_account_id = azurerm_storage_account.data.id

  rule {
    name    = "to-archive-after-days"
    enabled = true

    filters {
      prefix_match = ["backups/"]
      blob_types   = ["blockBlob"]
    }

    actions {
      base_blob {
        tier_to_archive_after_days_since_modification_greater_than = var.lifecycle_archive_after_days
      }
    }
  }
}

# Function app storage account (required for consumption plan)
resource "azurerm_storage_account" "function" {
  name                     = "stafunc${var.name_suffix_str}"
  resource_group_name      = data.azurerm_resource_group.main.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  
  allow_nested_items_to_be_public = false
  min_tls_version                 = "TLS1_2"

  tags = {
    Environment = "Production"
    Project     = "BackupAPI"
  }
}

# Log Analytics Workspace
resource "azurerm_log_analytics_workspace" "main" {
  name                = "law-${var.name_suffix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = var.log_analytics_retention_days
  daily_quota_gb      = var.log_analytics_daily_quota_gb

  tags = {
    Environment = "Production"
    Project     = "BackupAPI"
  }
}

# Application Insights
resource "azurerm_application_insights" "main" {
  name                = "appi-${var.name_suffix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.main.name
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.main.id

  tags = {
    Environment = "Production"
    Project     = "BackupAPI"
  }
}

# App Service Plan (Consumption)
resource "azurerm_service_plan" "main" {
  name                = "plan-${var.name_suffix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.main.name
  os_type             = "Linux"
  sku_name            = "Y1"

  tags = {
    Environment = "Production"
    Project     = "BackupAPI"
  }
}

# Function App
resource "azurerm_linux_function_app" "main" {
  name                = "func-${var.name_suffix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.main.name
  service_plan_id     = azurerm_service_plan.main.id

  storage_account_name       = azurerm_storage_account.function.name
  storage_account_access_key = azurerm_storage_account.function.primary_access_key

  identity {
    type = "SystemAssigned"
  }

  site_config {
    # Note: Using "8.0" here because Azure RM provider doesn't support "9.0" yet
    # The deployed .NET 9 code will still run correctly on Azure Functions v4
    application_stack {
      dotnet_version = "8.0"
      use_dotnet_isolated_runtime = true
    }
    
    application_insights_connection_string = azurerm_application_insights.main.connection_string
    application_insights_key               = azurerm_application_insights.main.instrumentation_key
  }

  app_settings = {
    "FUNCTIONS_EXTENSION_VERSION"     = "~4"
    "FUNCTIONS_WORKER_RUNTIME"        = "dotnet-isolated"
    "WEBSITE_RUN_FROM_PACKAGE"        = "1"
    "DATA_STORAGE_ACCOUNT"            = azurerm_storage_account.data.name
    "DATA_CONTAINER"                  = azurerm_storage_container.backups.name
    "API_KEY"                         = var.api_key
    # Explicitly prevent Python runtime confusion
    "WEBSITE_DISABLE_MSI"             = "false"
    "SCM_DO_BUILD_DURING_DEPLOYMENT"  = "false"
  }

  https_only = true

  tags = {
    Environment = "Production"
    Project     = "BackupAPI"
  }
}

# Role assignment for Function App to access data storage
resource "azurerm_role_assignment" "function_storage_contributor" {
  scope                = azurerm_storage_account.data.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_function_app.main.identity[0].principal_id
  
  depends_on = [azurerm_linux_function_app.main]
}

# Data source for existing resource group
data "azurerm_resource_group" "main" {
  name = "rg-fdev-weu-backup-prd"
}

# Data source for current Azure client config
data "azurerm_client_config" "current" {}

# Outputs
output "function_app_name" {
  description = "Name of the Function App"
  value       = azurerm_linux_function_app.main.name
}

output "data_storage_account_name" {
  description = "Name of the data storage account"
  value       = azurerm_storage_account.data.name
}

output "function_app_url" {
  description = "URL of the Function App"
  value       = "https://${azurerm_linux_function_app.main.default_hostname}"
}

output "container_name" {
  description = "Name of the storage container"
  value       = azurerm_storage_container.backups.name
}

output "log_analytics_workspace_id" {
  description = "ID of the Log Analytics workspace"
  value       = azurerm_log_analytics_workspace.main.id
}

output "log_analytics_workspace_name" {
  description = "Name of the Log Analytics workspace"
  value       = azurerm_log_analytics_workspace.main.name
}

output "application_insights_name" {
  description = "Name of the Application Insights resource"
  value       = azurerm_application_insights.main.name
}

output "application_insights_instrumentation_key" {
  description = "Instrumentation key of the Application Insights resource"
  value       = azurerm_application_insights.main.instrumentation_key
  sensitive   = true
}
