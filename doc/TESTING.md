# Testing guide

Run commands from the repository root.

## Test projects

The solution contains the following xUnit test projects:

| Project | Coverage |
| --- | --- |
| `tests/services/api/FlorisDeV.BackupApi.Tests.csproj` | API controllers, services, storage/state, event publishing, and security behavior |
| `tests/services/client/FlorisDeV.BackupClient.Tests.csproj` | client scanning, state, upload, encryption, restore, authentication, and integration flows |
| `tests/common/healthchecks/FlorisDeV.HealthChecks.Tests.csproj` | shared health checks |
| `tests/common/logging/FlorisDeV.Logging.Tests.csproj` | shared logging and telemetry helpers |
| `tests/services/gateway/FlorisDeV.Gateway.Tests.csproj` | Gateway HTTP forwarding, authentication failures, Swagger paths, cancellation, and upstream errors |
| `tests/services/monitoring/FlorisDeV.BackupMonitoring.Tests.csproj` | monitoring UI and administration behavior |

The worker's service tests live in the API test project. The Gateway and its test project are included in the solution. Gateway tests use the real ASP.NET pipeline with controlled upstream HTTP handlers; they do not contact Entra ID.

## Standard verification

```bash
dotnet restore FlorisDeV.BackupApi.sln
dotnet test FlorisDeV.BackupApi.sln --no-restore
dotnet build src/services/gateway/Gateway.csproj
```

The deploy workflow uses Release configuration:

```bash
dotnet test FlorisDeV.BackupApi.sln --configuration Release
dotnet build src/services/gateway/Gateway.csproj --configuration Release
```

## Run one project

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj
dotnet test tests/services/api/FlorisDeV.BackupApi.Tests.csproj
dotnet test tests/common/healthchecks/FlorisDeV.HealthChecks.Tests.csproj
dotnet test tests/common/logging/FlorisDeV.Logging.Tests.csproj
```

## Trait and name filters

Tests use xUnit `Category` traits where a unit/integration distinction is useful:

```bash
dotnet test FlorisDeV.BackupApi.sln --filter "Category=Unit"
dotnet test FlorisDeV.BackupApi.sln --filter "Category=Integration"
dotnet test FlorisDeV.BackupApi.sln --filter "FullyQualifiedName~FileSystemServiceTests"
dotnet test FlorisDeV.BackupApi.sln --filter "Category=Unit&FullyQualifiedName~BackupService"
```

Not every test necessarily carries a category, so the unfiltered solution run is the authoritative full suite.

Recovery regressions cover expired claims, retry eligibility and attempt limits, heartbeat renewal during manifest persistence, interruption between catalog publication and version retirement, lost encrypted-upload responses with a verified restore, incomplete scans, case-distinct path journaling, version 2 client-state migration, and client recovery from terminal commit failures. Filesystem permission tests run on Unix; Windows uses the case-insensitive path branch.

Several tests labelled Integration use temporary local files or SQLite. They do not require the Docker Compose stack or production Azure resources.

Local completion tests exercise the state interface with real SQLite files and reopen them to inspect durable outcomes. Coverage includes rollback after a later SQL failure, exact replay, stale or mismatched identities, empty/deletion-only runs, cancellation while waiting or before commit, and cancellation during commit. Private-database native hooks coordinate cancellation deterministically without adding a production test port. Client integration tests cover normal/resumed completion, rejection paging interruptions, partial success with zero accepted files, and invalid server identities.

Commit-request regressions exercise a lost response after completion, retries after failed queue publication, and a stale concurrent creator racing a worker that has already claimed and checkpointed the winning job. SQLite insert-if-absent tests use the real provider; Cosmos tests verify the create request and conflict/error handling with mocked SDK responses, without a live Cosmos account.

Device coordination tests cover competing jobs, independent devices, expired owners, and rejection of stale runs after a newer completion. Restore tests cover archived preparation, already-pending rehydration, preparation timeouts, renewed credentials, and integrity rejection. Archive tier behavior uses mocked storage responses because Azurite does not implement the archive service.

## End-to-end crash recovery and restore

On Linux with .NET 10, Python 3, and Docker:

```bash
python3 scripts/run-recovery-e2e.py --artifacts /tmp/stowhaven-recovery
```

Use a fresh artifact directory for each run. The harness publishes the actual client, API, and worker; starts isolated Azurite and Dapr containers; submits an encrypted backup while the worker is stopped; kills the worker mid-commit; restarts it after an expired claim; drops a later successful commit response; and restores the resulting incremental backup, including a deletion. It compares every restored plaintext SHA-256 with the source tree. Result JSON verifies pending versus completed CLI outcomes. All data and credentials are synthetic; repository `.env` and existing containers are not used. The harness removes its containers and processes and retains logs and `summary.json` under the artifact directory.

`.github/workflows/recovery-tests.yml` runs this check on relevant pull requests and main-branch pushes. Unit tests remain independent of Docker. Live Cosmos, Entra, and Azure archive rehydration still require a deployment smoke test.

## Docker Compose smoke test

For a runtime check after tests pass:

```bash
docker compose up --build -d
curl --fail http://localhost:8200/healthz
curl --fail http://localhost:8210/health/liveness
curl --fail http://localhost:8220/health/liveness
```

Then open the combined Swagger UI at `http://localhost:8200/swagger`. Local API and worker services use Development authentication and local infrastructure; this smoke test is not a substitute for validating Entra/OBO and Azure role assignments in a deployed environment.

## CI

`.github/workflows/deploy.yml` runs the solution tests, publishes API and worker artifacts, and builds the Gateway before the deployment and container-image phases. A local check should therefore include both the solution tests and the explicit Gateway build.
