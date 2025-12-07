# Data source for existing resource group
data "azurerm_resource_group" "main" {
  name = "rg-fdev-neu-backup-prd"
}

# Data source for current Azure client config
data "azurerm_client_config" "current" {}
