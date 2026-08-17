# Testing guide

Run commands from the repository root.

## Test projects

The solution currently contains four xUnit test projects:

| Project | Coverage |
| --- | --- |
| `tests/services/api/FlorisDeV.BackupApi.Tests.csproj` | API controllers, services, storage/state, event publishing, and security behavior |
| `tests/services/client/FlorisDeV.BackupClient.Tests.csproj` | client scanning, state, upload, encryption, restore, authentication, and integration flows |
| `tests/common/healthchecks/FlorisDeV.HealthChecks.Tests.csproj` | shared health checks |
| `tests/common/logging/FlorisDeV.Logging.Tests.csproj` | shared logging and telemetry helpers |

The worker is built by the solution but does not have a separate test project. The Gateway is not included in `FlorisDeV.BackupApi.sln` and currently has no automated test project, so build it explicitly.

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

Several tests labelled Integration use temporary local files or SQLite. They do not require the Docker Compose stack or production Azure resources.

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
