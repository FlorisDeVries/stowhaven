# GitHub Copilot Instructions

This project is an **Azure Backup API** in Python with IaC (Bicep) and CI/CD via GitHub Actions.

## Purpose of the codebase
- **Python Azure Function** that issues SAS URLs (upload/download) for Blob Storage.
- **Bicep templates** for:
  - Storage Account (LRS, Hot tier, lifecycle → Archive)
  - Container `backups`
  - Function App (Linux, Python 3.11, Consumption)
  - Managed Identity + RBAC
- **GitHub Actions** pipeline:
  - Login via OIDC
  - Deploy Bicep infra
  - Deploy Function code

## Style & guidelines
- Python code:
  - Use type hints
  - Follow Flake8 / PEP8 style
  - Use logging instead of print statements
- Bicep code:
  - Parameters for location, project name, lifecycle days
  - Resource names in lower-case, unique via `uniqueString()`
  - Define outputs for important values
- Commit messages in English, short but descriptive
- Always use `--recursive` for AzCopy directory uploads

## Copilot tips
- For Python Functions: provide examples of request payloads & responses.
- For Bicep extensions: have Copilot suggest linking new resources via `dependsOn`.
- For GitHub Actions: keep jobs small and reuse secrets through `env`.