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

  tags = local.common_tags
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

  tags = local.common_tags
}
