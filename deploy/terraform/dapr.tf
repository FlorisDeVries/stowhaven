# Redis Cache for DAPR State Store
resource "azurerm_redis_cache" "main" {
  name                = "redis-${var.name_suffix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.main.name
  capacity            = 1
  family              = "C"
  sku_name            = "Standard"
  
  non_ssl_port_enabled = false
  minimum_tls_version = "1.2"

  tags = local.common_tags
}

# Service Bus Namespace for DAPR Pub/Sub
resource "azurerm_servicebus_namespace" "main" {
  name                = "sb-${var.name_suffix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.main.name
  sku                 = "Standard"

  tags = local.common_tags
}

# Service Bus Topic for backup events
resource "azurerm_servicebus_topic" "backup_events" {
  name         = "backup-events"
  namespace_id = azurerm_servicebus_namespace.main.id

  partitioning_enabled = true
}

# Service Bus Topic Subscription for backup API
resource "azurerm_servicebus_subscription" "backup_api" {
  name     = "backup-api"
  topic_id = azurerm_servicebus_topic.backup_events.id

  max_delivery_count = 3
}

# Key Vault for DAPR Secrets
resource "azurerm_key_vault" "main" {
  name                = "kv-${var.name_suffix}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.main.name
  tenant_id          = data.azurerm_client_config.current.tenant_id
  sku_name           = "standard"

  # Enable RBAC
  enable_rbac_authorization = true
  
  # Network access
  network_acls {
    bypass         = "AzureServices"
    default_action = "Allow" # Change to "Deny" in production with proper network rules
  }

  tags = local.common_tags
}

# Grant Container App access to Key Vault
resource "azurerm_role_assignment" "container_app_key_vault" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_container_app.main.identity[0].principal_id
}

# Store API key in Key Vault
resource "azurerm_key_vault_secret" "api_key" {
  name         = "api-key"
  value        = var.api_key
  key_vault_id = azurerm_key_vault.main.id

  depends_on = [azurerm_role_assignment.container_app_key_vault]
}

# Store storage account name in Key Vault
resource "azurerm_key_vault_secret" "storage_account_name" {
  name         = "storage-account-name"
  value        = azurerm_storage_account.data.name
  key_vault_id = azurerm_key_vault.main.id

  depends_on = [azurerm_role_assignment.container_app_key_vault]
}

# Store data container name in Key Vault
resource "azurerm_key_vault_secret" "data_container" {
  name         = "data-container"
  value        = azurerm_storage_container.backups.name
  key_vault_id = azurerm_key_vault.main.id

  depends_on = [azurerm_role_assignment.container_app_key_vault]
}
