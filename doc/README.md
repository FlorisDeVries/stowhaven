# Stowhaven Documentation

The main project overview is in [../README.md](../README.md). This folder contains the detailed guides for architecture, deployment, client configuration, operations, and testing.

## Getting started

- [Client Configuration Guide](CLIENT_CONFIGURATION.md) - backup targets, common client settings, scheduling, and quick start.
- [.backupignore Reference](BACKUPIGNORE.md) - exclusion syntax and default ignore behavior.
- [GitHub Actions deployment setup](GITHUB_ACTIONS_DEPLOYMENT.md) - production deployment with Azure OIDC.

## Architecture and operations

- [Technical Design](TECHNICAL_DESIGN.md) - full architecture, flows, storage layout, state model, and security design.
- [Authentication](AUTHENTICATION.md) - Entra ID authentication and authorization model.
- [App Registrations](APP_REGISTRATIONS.md) - Entra application roles, scopes, credentials, and current deployment IDs.
- [Monitoring](MONITORING.md) - logs, metrics, health checks, and diagnostics.
- [Advanced Configuration](ADVANCED_CONFIGURATION.md) - performance tuning, resilience, encryption, and advanced client scenarios.
- [Testing Guide](TESTING.md) - test strategy and client testing instructions.

## Cost reference

- [Cost estimate artifacts](costs/) - a historical calculator export and screenshot; reprice before making budget decisions.
