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
    "DATA_STORAGE_ACCOUNT"            = azurerm_storage_account.data.name
    "DATA_CONTAINER"                  = azurerm_storage_container.backups.name
    "API_KEY"                         = var.api_key
    # Enable build during deployment for .NET apps
    "SCM_DO_BUILD_DURING_DEPLOYMENT"  = "true"
    "ENABLE_ORYX_BUILD"               = "true"
    # Explicitly prevent Python runtime confusion
    "WEBSITE_DISABLE_MSI"             = "false"
  }

  https_only = true

  tags = local.common_tags
}
