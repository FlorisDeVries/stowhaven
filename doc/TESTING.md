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
bash scripts/verify.sh
```

This is the shared local/CI entry point. It restores dependencies, builds the complete Release solution (including Gateway), publishes the monitoring UI, and runs the .NET suite, Python packaging tests, and workflow automation tests. It stops at the first failure and prints the full log's path plus a bounded tail. Logs and publish output are retained under the OS temporary directory (`TMPDIR`, falling back to `/tmp`). CI prints full logs in collapsed groups.

For infrastructure changes, add Bicep compilation without writing generated output into the working tree:

```bash
bash scripts/verify.sh --bicep
```

During TDD, restore before the first baseline and use the focused project/filter commands below for subsequent loops. Run full verification after the review fixes. The standard checks require .NET 10, Python 3 and Bash; Bicep compilation additionally needs Azure CLI/Bicep. Docker is only needed for the separate recovery and smoke checks.

## Workflow automation tests

```bash
python3 -m unittest discover -s tests/workflow -p 'test_*.py' -v
```

These tests use disposable repositories and a local bare remote to verify protected-branch commits, push destination refs, linked worktrees, branch-independent installation, and preservation of custom hooks. The verification runner is exercised with controlled tool executables to check ordering, temporary output paths and fail-fast behavior. They do not modify the current clone's hooks.

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

`.github/workflows/deploy.yml` calls `bash scripts/verify.sh` for CI, with full logs retained in collapsed job-log groups. The main-branch deployment jobs retain their separate infrastructure validation and deployment steps. `.github/workflows/recovery-tests.yml` runs the container-based recovery check independently.

## Installed companion with real systemd

The complete Linux demonstration uses the same genuine recovery harness with
fresh client/API/worker publications, synthetic targets, Azurite, Dapr, and the
actual systemd user manager. It requires Docker access and a running user
session; it never starts or changes the installed `backup-client` units.

```bash
python3 scripts/run-recovery-e2e.py --dashboard-smoke --single-file-client \
  --prepare-only --artifacts /tmp/stowhaven-dashboard-check
```

Use a new artifact directory. Preparation writes private binaries/configuration,
a hash-bound single-file capability manifest, `preflight.json`, explicit unit
files and an environment-scrubbing launcher. Inspect the exact paths, loopback
endpoints, synthetic targets, unique compile-time user-secrets identity, unique
unit/container/app IDs and persistent link ownership before running:

```bash
python3 scripts/run-recovery-e2e.py --dashboard-smoke --single-file-client \
  --resume-prepared --artifacts /tmp/stowhaven-dashboard-check
```

Wait for `ready-for-browser` in the artifact directory. In another terminal,
load `environment.json` into the installed open command's environment (rather
than inheriting backup/authentication configuration), and open its local secret
grant without logging or capturing it. The exact `<prefix>-open-dashboard`
command is under the artifact directory's `bin`; the prefix is in `preflight.json`.
Use its `--print-url` option when opening manually on the same machine.

Authorize the browser, inspect idle scheduling/history/log controls, click
**Start backup**, observe the active attempt, and reload the tab. The harness
holds only its own local proxy's first start-run request, before forwarding it,
so the real client remains active. After that browser check, create
`browser-check-complete` in the artifact directory. The browser gate lasts at
most four minutes and the held request at most two minutes after Start.

The harness verifies actual persistent timer enablement and activation,
HTTP/manual CLI/timer contention, execution receipts matching the manager's
real invocation, and disabling/removing/reinstalling the companion during active
and pending work. It then runs the genuine server/worker recovery, lost-response,
local-completion and encrypted-restore checks; kills/restarts the real client;
preserves unsupported newer reporting through reader/producer restarts; rejects
resident scheduling; and diagnoses an actually unavailable test bus without
stopping the user's manager. A separately launched observer for the unavailable
bus is an owned child using the same private publication.

Finally it stops only manifest-owned units, removes only exact matching test
links, reloads the manager, removes only its own containers and kills only its
own children. SIGINT, SIGTERM and exceptions enter cleanup. Artifacts remain
private; `summary.json`, `dashboard-summary.json`, `dashboard-cleanup.json` and
the browser check provide evidence. Do not publish private configuration, raw
logs, recovery material or authorization grants. If execution fails after a
backup starts, preserve the artifacts and prepare a fresh directory for the next
complete run; its pending state refers to the discarded synthetic emulator.

Focused installer checks use synthetic command fixtures and a real read-only
systemd unit parser; they do not establish genuine manager lifecycle behavior:

```bash
python3 -m unittest discover -s tests/deployment -p test_client_installation.py
```

Existing dashboard tests separately verify retained history, diagnostic bounds,
redaction, authorization expiry, stale/interrupted observations and freshness
with controlled time. Run them and the shared `scripts/verify.sh` alongside this
real-system demonstration. The standalone recovery command remains available
without systemd or browser interaction.
