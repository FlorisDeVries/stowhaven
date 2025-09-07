# GitHub Copilot Instructions

This project is an **Azure Backup API** in Python with IaC (Terraform) and CI/CD via GitHub Actions.

## Purpose of the codebase
- **Python Azure Function** that issues SAS URLs (upload/download) for Blob Storage.
- **Terraform configuration** for:
  - Storage Account (LRS, Cool tier, lifecycle → Archive)
  - Container `backups`
  - Function App (Linux, Python 3.11, Consumption)
  - Managed Identity + RBAC
  - Remote state backend in Azure Storage
- **GitHub Actions** pipeline:
  - Login via OIDC
  - Deploy Terraform infra
  - Deploy Function code

## Style & guidelines
- Python code:
  - Use type hints
  - Follow Flake8 / PEP8 style
  - Use logging instead of print statements
- Terraform code:
  - Use variables for location, name suffixes, lifecycle days
  - Resource names in lower-case with consistent naming convention
  - Use data sources for existing resources (like resource group)
  - Define outputs for important values
  - Use remote backend for state management
  - Pin provider versions (e.g., `~> 3.100`)
- Commit messages in English, short but descriptive
- Always use `--recursive` for AzCopy directory uploads

## Copilot tips
- For Python Functions: provide examples of request payloads & responses.
- For Terraform: suggest using `depends_on` for explicit resource dependencies.
- For GitHub Actions: keep jobs small and reuse secrets through `env`.
- For authentication: prefer managed identity over connection strings when possible.