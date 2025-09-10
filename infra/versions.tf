terraform {
  required_version = ">= 1.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.100"
    }
  }
  
  backend "azurerm" {
    resource_group_name  = "rg-fdev-weu-backup-prd"
    storage_account_name = "staterraformfdevweuprd"
    container_name       = "tfstate"
    key                  = "backup-api.tfstate"
  }
}

provider "azurerm" {
  features {}
}
