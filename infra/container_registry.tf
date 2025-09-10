# Azure Container Registry
resource "azurerm_container_registry" "main" {
  name                = "acr${var.name_suffix_str}"
  resource_group_name = data.azurerm_resource_group.main.name
  location            = var.location
  sku                 = "Basic"
  admin_enabled       = true

  tags = local.common_tags
}
