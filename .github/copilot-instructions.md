# GitHub Copilot instructions

This repository contains **Stowhaven**, a .NET 10 backup platform deployed to Azure Container Apps.

## Architecture

- The public Gateway authenticates Microsoft Entra ID tokens, performs OAuth on-behalf-of exchange, and proxies requests to the internal API and worker.
- The API issues short-lived User Delegation SAS URLs, manages devices and backup runs, and publishes commit events through a Dapr Azure Storage Queue output binding.
- The worker consumes commit events through a Dapr input binding and finalizes staged files.
- Clients transfer file data directly to Azure Blob Storage.
- Application state uses SQLite locally and Azure Cosmos DB for NoSQL in production through `IStateDocumentStore`; it does not use a Dapr state-store component.
- Infrastructure is defined in `deploy/bicep/`. CI/CD is the multi-phase `.github/workflows/deploy.yml` workflow and publishes images to GHCR.

## Compatibility names

Stowhaven is the public product and repository brand. Keep existing implementation contracts stable unless a migration is explicitly requested, including:

- `FlorisDeV.Backup*` solution, project, assembly, and namespace names;
- `BackupApiClient` configuration keys;
- Dapr app IDs, container image suffixes, and telemetry service names such as `backup-api`;
- `backup-client` local paths, executable link, and systemd unit names;
- existing Azure resource names and Entra application IDs.

## Engineering guidelines

- Preserve nullable reference types and current .NET conventions.
- Prefer structured `ILogger` messages and asynchronous APIs.
- Keep public traffic on the Gateway; production API and worker ingress must remain internal.
- Prefer managed identity and RBAC over storage account keys or connection strings.
- Treat direct-to-Blob upload/restore and client-side encryption as core security boundaries.
- Update Bicep source and regenerate `deploy/bicep/main.json` together.
- Build the Gateway explicitly because it is not included in `FlorisDeV.BackupApi.sln`.
- Keep documentation aligned with implemented behavior and distinguish current limitations from planned work.
