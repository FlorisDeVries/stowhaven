# Container App Environment
resource "azurerm_container_app_environment" "main" {
  name                       = "cae-${var.name_suffix}"
  location                   = var.location
  resource_group_name        = data.azurerm_resource_group.main.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id

  tags = local.common_tags
}

# Container App
resource "azurerm_container_app" "main" {
  name                         = "ca-${var.name_suffix}"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"

  identity {
    type = "SystemAssigned"
  }

  template {
    min_replicas = 0
    max_replicas = 10

    container {
      name   = "backup-api"
      image  = "${azurerm_container_registry.main.login_server}/backup-api:latest"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "DATA_STORAGE_ACCOUNT"
        value = azurerm_storage_account.data.name
      }

      env {
        name  = "DATA_CONTAINER"
        value = azurerm_storage_container.backups.name
      }

      env {
        name        = "API_KEY"
        secret_name = "api-key"
      }

      env {
        name  = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        value = azurerm_application_insights.main.connection_string
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_container_app.main.identity[0].principal_id
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "http"

    traffic_weight {
      percentage      = 100
      latest_revision = true
    }
  }

  secret {
    name  = "api-key"
    value = var.api_key
  }

  registry {
    server   = azurerm_container_registry.main.login_server
    username = azurerm_container_registry.main.admin_username
    password_secret_name = "acr-password"
  }

  secret {
    name  = "acr-password"
    value = azurerm_container_registry.main.admin_password
  }

  tags = local.common_tags
}
