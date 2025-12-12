# Role assignment for Container App to access data storage
resource "azurerm_role_assignment" "container_app_storage_contributor" {
  scope                = azurerm_storage_account.data.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_container_app.main.identity[0].principal_id
  
  depends_on = [azurerm_container_app.main]
}

# Role assignment for Container App to pull from ACR
resource "azurerm_role_assignment" "container_app_acr_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_container_app.main.identity[0].principal_id
  
  depends_on = [azurerm_container_app.main]
}

# Role assignment for Container App to delegate storage operations
resource "azurerm_role_assignment" "container_app_storage_delegator" {
  scope                = azurerm_storage_account.data.id
  role_definition_name = "Storage Blob Delegator"
  principal_id         = azurerm_container_app.main.identity[0].principal_id
}