output "container_app_name" {
  description = "Name of the Container App"
  value       = azurerm_container_app.main.name
}

output "data_storage_account_name" {
  description = "Name of the data storage account"
  value       = azurerm_storage_account.data.name
}

output "container_app_url" {
  description = "URL of the Container App"
  value       = "https://${azurerm_container_app.main.ingress[0].fqdn}"
}

output "container_registry_name" {
  description = "Name of the Container Registry"
  value       = azurerm_container_registry.main.name
}

output "container_registry_login_server" {
  description = "Login server URL of the Container Registry"
  value       = azurerm_container_registry.main.login_server
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
