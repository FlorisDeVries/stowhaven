# GitHub Copilot Instructions

This project is an **Azure Backup API** in .NET with IaC (Terraform) and CI/CD via GitHub Actions.

## Purpose of the codebase
- **.NET Azure Function** that issues SAS URLs (upload/download) for Blob Storage.
- **Terraform configuration** for:
  - Storage Account (LRS, Cool tier, lifecycle → Archive)
  - Container `backups`
  - Function App (Linux, .NET 8.0, Consumption)
  - Managed Identity + RBAC
  - Remote state backend in Azure Storage
- **GitHub Actions** pipeline:
  - Login via OIDC
  - Deploy Terraform infra
  - Deploy Function code

## Style & guidelines
- .NET code:
  - Use C# 12 features and nullable reference types
  - Follow Microsoft coding conventions
  - Use structured logging with ILogger
  - Prefer async/await patterns
  - Use dependency injection where appropriate
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
- For .NET Functions: provide examples of request payloads & responses.
- For Terraform: suggest using `depends_on` for explicit resource dependencies.
- For GitHub Actions: keep jobs small and reuse secrets through `env`.
- For authentication: prefer managed identity over connection strings when possible.