# GitHub Copilot Instructions

This project is an **Azure Backup API** in .NET with IaC (Bicep) and CI/CD via GitHub Actions.

## Purpose of the codebase
- **.NET Container App** that issues SAS URLs (upload/download) for Blob Storage.
- **Bicep configuration** (`deploy/bicep/`) for:
  - Storage Account (LRS, Cool tier, lifecycle → Archive) + `backups` container
  - Log Analytics Workspace + Application Insights
  - Azure Container Registry (Basic)
  - Dapr infrastructure: Redis Cache, Service Bus, Key Vault
  - Container App Environment + Container App (system-assigned MI, Dapr enabled)
  - RBAC role assignments (Storage Blob Data Contributor, AcrPull, Key Vault Secrets User)
- **GitHub Actions** pipeline (`.github/workflows/deploy.yml`):
  - Login via OIDC
  - Deploy Bicep infra (`az deployment group create`)
  - Build & push Docker image to ACR
  - Update Container App with new image

## Bicep structure
```
deploy/bicep/
├── main.bicep            # Orchestrator – calls modules, creates role assignments & KV secrets
├── main.bicepparam       # Parameter defaults
└── modules/
    ├── storage.bicep     # Storage accounts, container, lifecycle policy
    ├── monitoring.bicep  # Log Analytics, Application Insights
    ├── registry.bicep    # Azure Container Registry
    ├── dapr-infra.bicep  # Redis, Service Bus, Key Vault (no secrets)
    └── compute.bicep     # Container App Environment + Container App + Dapr component
```

## Style & guidelines
- .NET code:
  - Use C# 12 features and nullable reference types
  - Follow Microsoft coding conventions
  - Use structured logging with ILogger
  - Prefer async/await patterns
  - Use dependency injection where appropriate
- Bicep code:
  - Use `param` with `@description()` decorators; mark secrets with `@secure()`
  - Pre-determine resource names in `main.bicep` (e.g. `var keyVaultName = 'kv-${nameSuffix}'`) to avoid circular module dependencies
  - Use `existing` resources to reference pre-existing Azure resources
  - Use `dependsOn` for secrets that require prior role assignments
  - Use `guid()` for deterministic role assignment names
  - Use system-assigned managed identity; avoid connection strings
- Commit messages in English, short but descriptive
- Always use `--recursive` for AzCopy directory uploads

## Copilot tips
- For .NET Container Apps: provide examples of request payloads & responses.
- For Bicep: use `subscriptionResourceId()` to reference built-in role definitions.
- For GitHub Actions: keep jobs small and reuse secrets through `env`.
- For authentication: prefer managed identity over connection strings when possible.