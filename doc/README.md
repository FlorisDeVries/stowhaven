# Azure Backup API

This project is a lightweight, low-cost backup service built on **Azure Container Apps**, **Blob Storage**, **Dapr**, and **.NET**.
It securely issues **temporary SAS URLs** that allow clients to upload encrypted files directly to Azure Blob Storage using a **scoped, time-limited** data path.

The service supports:

* 🔒 **Zero-trust client access** using User Delegation SAS (no account keys exposed)
* 🚀 **High-performance uploads** (client → Blob directly)
* 📦 **Incremental backups** (only changed files uploaded)
* 🔄 **Async commit pipeline** via Dapr pub/sub and background worker
* 📁 **Centralized manifest/state** stored in Azure Table Storage
* 🧊 **Lifecycle policies** automatically moving old backups to Archive to reduce cost
* 🌩️ Full **Infrastructure-as-Code** (Bicep) and **CI/CD** (GitHub Actions)

This repository includes everything needed to deploy and operate the backup API and its supporting infrastructure.

---

## 📦 Project Structure

```
.
├── src/
│   ├── services/api/                 # Backup API (ACA) issuing SAS, handling backup runs
│   │   ├── Controllers/              # HTTP endpoints
│   │   ├── Services/                 # SAS minting, run management, validation
│   │   ├── Models/                   # API & domain models
│   │   ├── Constants/                # App-wide constants
│   │   ├── Program.cs                # API entry point
│   │   ├── ProgramExtensions.cs      # DI + configuration
│   │   └── FlorisDeV.BackupApi.csproj
│   ├── services/worker/              # Commit Worker (ACA Job)
│   │   ├── ...                       # Processes commit-run jobs from queue
│   └── common/                       # Shared code across API + worker
│       ├── featureflags/
│       ├── healthchecks/
│       └── logging/
│
├── deploy/bicep/                     # Infrastructure-as-Code
│   ├── main.bicep                    # Root deployment
│   ├── main.bicepparam               # Parameter defaults
│   └── modules/                      # Storage, compute, registry, monitoring, Dapr infra
│
├── tests/                            # Tests for API + worker
├── .github/workflows/                # GitHub Actions pipelines
│   ├── build.yml
│   ├── infrastructure.yml
│   ├── deploy.yml
│   └── full-pipeline.yml
│
├── run/                              # Local Dapr configuration
├── doc/                              # Architecture & design documentation
└── docker-compose.yml                # Local container-based dev environment
```

---

## 🗂️ Backup Flow (High-Level)

1. **Client authenticates** using Entra ID
2. **API issues a directory-scoped SAS** for `staging/{deviceId}/{runId}/`
3. **Client uploads changed/new files** directly to Blob
4. **Client uploads run-manifest.json** describing all changes
5. Client calls **/commit-run** with only:

   ```json
   { "deviceId": "...", "runId": "...", "manifestBlobPath": "..." }
   ```
6. API enqueues a **commit job**
7. The **Commit Worker**:

   * Loads manifest
   * Validates blobs
   * Moves them into `/files/`
   * Retires old versions into `/retired/`
   * Updates manifest/state via Dapr
8. Blob lifecycle rules automatically move retired data to **Archive tier** after 30+ days

---

## 🧊 Lifecycle Rule

All blobs in `backups/devices/**/files/` are automatically promoted to **Archive tier after 30 days** without modification, minimizing long-term storage costs.

Retired versions under `backups/devices/**/retired/` are deleted after the Archive retention window (≈ 180–210 days).

---

## 📖 Documentation

### Getting Started
- **[Client Configuration Guide](CLIENT_CONFIGURATION.md)** - Quick start and essential configuration
- **[.backupignore Reference](BACKUPIGNORE.md)** - File exclusion patterns for different scenarios

### Advanced Topics
- **[Advanced Configuration](ADVANCED_CONFIGURATION.md)** - Performance tuning, resilience, and complex scenarios
- **[Technical Design](TECHNICAL_DESIGN.md)** - Complete architecture, flows, and security model
- **[Authentication](AUTHENTICATION.md)** - Authentication and authorization details
- **[Monitoring](MONITORING.md)** - Observability, metrics, and diagnostics
- **[Testing Guide](TESTING.md)** - How to test the backup client

### Reference
- **[Cost Analysis](costs/)** - Storage costs and optimization strategies