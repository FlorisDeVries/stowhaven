variable "location" {
  description = "Azure region for resources"
  type        = string
  default     = "northeurope"
}

variable "name_suffix" {
  description = "Suffix for resource names"
  type        = string
  default     = "fdev-neu-prd"
}

variable "name_suffix_str" {
  description = "Suffix for storage account names (no dashes)"
  type        = string
  default     = "fdevneuprd"
}

variable "lifecycle_archive_after_days" {
  description = "Days after which blobs are moved to archive tier"
  type        = number
  default     = 30
}

variable "api_key" {
  description = "API key for authenticating requests to the backup API"
  type        = string
  sensitive   = true
}

variable "log_analytics_retention_days" {
  description = "Retention period for Log Analytics workspace in days"
  type        = number
  default     = 30
}

variable "log_analytics_daily_quota_gb" {
  description = "Daily ingestion quota for Log Analytics workspace in GB"
  type        = number
  default     = 1
}
