# Role assignment for Function App to access data storage
resource "azurerm_role_assignment" "function_storage_contributor" {
  scope                = azurerm_storage_account.data.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_function_app.main.identity[0].principal_id
  
  depends_on = [azurerm_linux_function_app.main]
}
