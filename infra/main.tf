# Backup API Infrastructure
# This is the main entry point for the Terraform configuration.
# Individual resource definitions are organized in separate files:
#
# - versions.tf      : Terraform and provider version requirements
# - variables.tf     : Input variable definitions  
# - locals.tf        : Local value definitions
# - data.tf          : Data source definitions
# - storage.tf       : Storage account and container resources
# - monitoring.tf    : Log Analytics and Application Insights
# - function_app.tf  : Function App and App Service Plan
# - iam.tf           : Role assignments and permissions
# - outputs.tf       : Output value definitions
