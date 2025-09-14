# DEPRECATED: Function App resources - replaced with Container Apps
# This file is kept for reference during migration and will be removed after successful deployment

# The function app resources have been replaced with:
# - container_registry.tf: Azure Container Registry
# - container_app.tf: Container App Environment and Container App

# Original Function App configuration is preserved below as comments for reference:

/*
# App Service Plan (Consumption)
resource "azurerm_service_plan" "main" {
  name                = "plan-${var.name_suffix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.main.name
  os_type             = "Linux"
  sku_name            = "Y1"

  tags = local.common_tags
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
    "DATA_STORAGE_ACCOUNT"            = azurerm_storage_account.data.name
    "DATA_CONTAINER"                  = azurerm_storage_container.backups.name
    "API_KEY"                         = var.api_key
    "SCM_DO_BUILD_DURING_DEPLOYMENT"  = "true"
    "ENABLE_ORYX_BUILD"               = "true"
    "WEBSITE_DISABLE_MSI"             = "false"
  }

  https_only = true

  tags = local.common_tags
}
*/
