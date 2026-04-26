Verdict
The project is still not production-ready yet outside CI/CD, but the original highest-risk architecture gaps have been addressed. The foundation is good: clean .NET structure, tests pass, Dapr/state/pubsub concepts are present, observability is considered, and the design is directionally sound. The client/API staging protocol, run manifest upload, deletion-only commits, local-state-after-commit flow, device ownership, worker split, server-side staged blob validation, commit idempotency, and production infrastructure wiring are now implemented. Remaining production gaps are mostly client productionization, restore, operational tooling, SaaS administration, and deeper manifest/state hardening.

Validation run:

dotnet test FlorisDeV.BackupApi.sln --no-restore --verbosity minimal
Result: 400 tests passed, 0 failed
Bicep validation was skipped because Azure CLI is not available in the environment.
P0 production blockers
1. Client and API backup protocol are incompatible — addressed
The design says the client should:

Start a run.
Upload changed files to staging/{deviceId}/{runId}/{uniqueFileId}.
Upload runs/{deviceId}/{runId}/run-manifest.json.
Commit the run with the manifest path.
The implementation now does this.

Current client behavior:

Uploads changed blobs under staging/{deviceId}/{runId}/{uniqueFileId}.
Generates server-compatible uniqueFileId values.
Uploads runs/{deviceId}/{runId}/run-manifest.json.
Calls commit with runId only; the API and worker derive the manifest path from deviceId/runId.
Relevant locations:

Upload path uses uniqueFileId when present: FileUploader.cs
Commit uploads manifest and sends only runId: BackupService.cs
Worker reads manifest at runs/{deviceId}/{runId}/run-manifest.json: BackupProcessingService.cs
Worker moves staged source blob by uniqueFileId: BackupProcessingService.cs
Manifest path caller-control risk is addressed by deriving paths server-side.

2. Deletion-only backups are not committed to the API — addressed
If a run has only deleted files and no changed/new files, the client now starts a deletion-only run, uploads a manifest with deleted logical paths, commits the run, polls commit status, and only then removes local deleted-file state.

Relevant locations:

Deleted files are detected after scanning/upload processing: BackupService.cs
Deletion-only runs are started via StartDeletionOnlyRunAsync: BackupService.cs
Deleted logical paths are included in run-manifest.json: BackupService.cs
Local deleted state is removed after commit-status returns Succeeded: BackupService.cs
Impact: server-side manifest/state is now updated for deletion-only changes before local cleanup is finalized.

3. Local state is updated before server commit succeeds — addressed
The client no longer saves uploaded files to the local SQLite state immediately after upload. Successfully uploaded files are tracked in memory and written to local state only after the API commit job reports Succeeded.

Relevant locations:

Batch upload records successful uploads in UploadedChangedFiles only: BackupService.cs
CommitBackupAsync uploads the manifest, commits, polls GetCommitStatus, and only then writes local file state: BackupService.cs
Deleted file cleanup also happens only after commit success: BackupService.cs
Impact: if commit fails or times out, local file/deletion state is not advanced as successful.

Remaining recommendation: add durable PendingUpload / PendingCommit state to support process restarts while a commit is still queued or processing.

4. Authentication does not bind users to devices/customers — addressed for self-service single-owner devices
The API now uses a server-side device registration model. Any authenticated user with the client backup scope can self-register a new device, but an existing device ID cannot be claimed by a different user. Backup operations use device-scoped routes and authorize ownership before starting runs, committing runs, or returning commit status.

Relevant locations:

Self-service registration endpoint: DevicesController.cs
Device ownership and registration state: DeviceRegistration.cs
Registration/ownership checks: DeviceRegistryService.cs
Device registry Dapr component: device-registry-state-store
Backup routes authorize route deviceId before work starts: BackupController.cs
JWT validation accepts the narrower backup.client scope: JwtBearerAuthenticationHandler.cs
Client registers the local device before backup: BackupService.cs
Impact: users can manage backups only for devices they own; route deviceId is authoritative and request-body spoofing is removed.

Implemented model:

tenantId
userId
deviceId
displayName
status
last seen
revocation state
Device registrations are stored separately from backup manifest/run state so a future UI can add user/device indexes without mixing identity metadata with commit-processing state. Remaining recommendation: later licensing/customer support can gate device registration and add customerId, max-device policies, sharing, and admin workflows.

5. Production secret/config wiring is broken or inconsistent — addressed
BlobStorageService still retrieves DATA_STORAGE_ACCOUNT and DATA_CONTAINER through ISecretService, but SecretService now resolves values from IConfiguration/environment variables before falling back to Dapr secret store. This matches the Bicep deployment, where DATA_STORAGE_ACCOUNT and DATA_CONTAINER are configured as Container App environment variables.

Relevant locations:

BlobStorageService asks ISecretService for DATA_STORAGE_ACCOUNT and DATA_CONTAINER: BlobStorageService.cs:75-80
SecretService checks IConfiguration/environment variables first, then Dapr GetSecretAsync: SecretService.cs
Bicep sets DATA_STORAGE_ACCOUNT and DATA_CONTAINER as environment variables: compute.bicep:165-174
Unused mismatched Key Vault secrets for storage-account-name and data-container were removed: main.bicep
Impact: production blob storage initialization can now use the configured environment variables without requiring those non-secret values to exist in Key Vault.

Remaining recommendation: keep non-secret resource names in configuration/environment variables and reserve Dapr secret store/Key Vault for actual secrets.
6. Commit worker is not actually separate from the API — addressed with a separate worker project and scale-to-zero app
The API and commit worker now run as separate projects and separate Azure Container Apps. The public API project no longer contains the Dapr backup event controller. The worker project owns the Dapr backup event endpoint and processes commit messages.

Relevant location:

Commit processing endpoint is owned by the worker project: BackupEventsController.cs
Worker host: Program.cs
API Container App role: compute.bicep
Worker Container App role and Service Bus scale rule: compute.bicep
Local docker-compose worker: docker-compose.yml
Impact:

Long commits no longer occupy public API replicas.
API traffic and commit processing can scale independently.
The worker scales from zero based on the Service Bus topic subscription, so idle cost stays near zero.
The API and worker use separate images, but the worker image reuses the API service layer through a project reference.
Remaining recommendation: verify Service Bus scaler behavior after deployment and tune maxReplicas, messageCount, timeoutInSec, and maxConcurrentHandlers for real backup sizes.

P1 critical reliability and data integrity issues
7. Server does not verify uploaded blob size/hash before commit — addressed
The worker now validates each staged blob before moving it into the committed file location. The client writes SHA-256 metadata on staged uploads, and the worker checks blob properties before state changes.

Relevant locations:

Client stores staged blob metadata: FileUploader.cs
Shared metadata keys: BackupBlobMetadata.cs
Worker validates staged blob HEAD/properties before move: BackupProcessingService.cs

Current validation:

Checks the staged blob exists.
Validates ContentLength == manifest.Size.
Validates backup_sha256 metadata == manifest.Sha256.
Fails the commit before moving the blob if validation fails.

Remaining recommendation: add optional transactional checksum headers or full server-side hash verification for a future paranoid mode if the additional read cost is acceptable.
8. Blob move fallback can cause early deletion fees and partial failures — addressed
The design relies on ADLS Gen2 rename to avoid early deletion penalties. The implementation no longer silently falls back to copy/delete in Azure production paths.

Relevant location:

BlobStorageService.MoveBlobAsync now treats ADLS rename failure as fatal unless ALLOW_COPY_DELETE_FALLBACK is explicitly true: BlobStorageService.cs
Bicep exposes allowCopyDeleteFallback and maps it to ALLOW_COPY_DELETE_FALLBACK for API and worker containers: main.bicep, compute.bicep

Current behavior:

Azurite/local development still uses copy/delete because ADLS rename is unavailable.
Azure production first attempts ADLS Gen2 rename.
If rename fails and ALLOW_COPY_DELETE_FALLBACK is not true, the move fails and logs a Critical event.
If fallback is explicitly enabled, the move logs an Error and continues with copy/delete.
Fallback copy uses create-only destination conditions to avoid overwriting an existing destination blob.

9. Commit processing is not transactionally safe — addressed
ProcessFileEntryAsync moves the staged blob, saves a new FileVersion, retires the old version, then saves FileEntry.

Mitigation:

- Commit processing records per-file progress in the manifest state store.
- The worker uses deterministic progress transitions: Pending → Moved → StateUpdated → Succeeded.
- Failed file commits are recorded with error details and can be retried.
- A retry can continue when the destination blob already exists and the source staging blob is gone.

Relevant locations:

- Per-file progress contract: src/common/contracts/State/CommitJob.cs
- Per-file progress state methods: src/common/core/Services/StateStoreManager.cs
- Idempotent worker flow: src/services/worker/Services/BackupProcessingService.cs

Remaining recommendation: add dedicated repair/reconciliation tooling for long-lived failed commit progress records.

10. Duplicate commits are not deduplicated by deviceId + runId — addressed
CreateCommitJobAsync now derives a deterministic commit ID from {deviceId, runId} and returns the existing CommitJob if it already exists.

Relevant location:

Commit idempotency key: src/common/core/Services/StateStoreManager.cs

Impact: retrying /commit-run for the same runId returns the same commit job instead of creating duplicate work.

11. Concurrency handling is incomplete — addressed
The worker now atomically claims queued commit jobs through an ETag compare-and-save operation.

Relevant locations:

- Atomic claim method: src/common/core/Services/StateStoreManager.cs
- Worker claim use: src/services/worker/Services/BackupProcessingService.cs

Impact: if two consumers receive the same event, only one can transition Queued → Processing. The other skips without doing blob/state work.

12. Uploads overwrite blobs — addressed
The client now uses create-only BlobUploadOptions for all file uploads, including legacy logical-path uploads and unique-file uploads.

Relevant location:

Create-only uploads: src/services/client/Services/FileUploader.cs

This conflicts with the design’s intent to use If-None-Match: * and immutable unique blob names.

Mitigation: every upload uses BlobRequestConditions { IfNoneMatch = ETag.All }; unique-file uploads also include SHA-256 and unique-file metadata.

P1 security issues
13. ManifestBlobPath is not validated — addressed
ManifestBlobPath is no longer accepted from the client and the API no longer forwards caller-supplied manifest paths.

Current behavior:

- CommitBackupRunRequest contains only runId.
- BackupController passes only route deviceId and request runId to the service.
- BackupRunService and BackupEventPublisher derive the manifest path from deviceId/runId.
- BackupProcessingService also derives the manifest path from the event deviceId/runId and ignores any ManifestPath field on the event.

The only accepted manifest location is:

runs/{deviceId:N}/{runId:N}/run-manifest.json

14. Forwarded headers trust all proxies — addressed
Forwarded headers no longer clear trusted proxy/network restrictions by default.

Current behavior:

- Only X-Forwarded-For, X-Forwarded-Proto, and X-Forwarded-Host are enabled.
- ForwardLimit defaults to 1.
- KnownProxies and KnownNetworks remain enforced by ASP.NET Core and can be configured under ReverseProxy:ForwardedHeaders.
- Unknown client-supplied forwarded headers are ignored instead of trusted globally.

Relevant location: ProgramExtensions.cs

15. SAS IP restriction may break real customer clients — addressed
SAS IP restriction is disabled by default for the SaaS API because customers may use changing residential IPs, CGNAT, VPNs, or proxy paths.

Current behavior:

- BackupController passes no client IP to SAS minting unless Backup:Sas:EnableIpRestriction is true.
- Bicep exposes enableSasIpRestriction, default false, mapped to Backup__Sas__EnableIpRestriction.
- Short TTL, create-only SAS, and device/run-scoped paths remain the default protection.

Relevant locations: BackupController.cs, SasSecurityOptions.cs, main.bicep, compute.bicep

16. Development anonymous auth is dangerous if environment is misconfigured — addressed
Development anonymous authentication now requires an explicit process environment variable.

Current behavior:

- If ASPNETCORE_ENVIRONMENT=Development but ALLOW_DEVELOPMENT_ANONYMOUS_AUTHENTICATION is not true, startup fails.
- docker-compose sets ALLOW_DEVELOPMENT_ANONYMOUS_AUTHENTICATION=true for local API development only.
- Production Bicep sets ASPNETCORE_ENVIRONMENT=Production and does not set the anonymous-auth override.

Relevant locations: HostBuilderExtensions.cs, docker-compose.yml

P1 infrastructure issues
17. Bicep still contains an unused API key model — addressed
The deployment is JWT/Entra-based and no API-key authentication path is used. The stale API-key model has been removed from Bicep.

Current behavior:

- main.bicep no longer accepts a secure apiKey parameter.
- compute.bicep no longer creates api-key Container App secrets.
- API and worker containers no longer receive an API_KEY environment variable.
- main.bicep no longer creates an api-key Key Vault secret.

18. Service Bus roles are incomplete for a subscriber — addressed
The architecture now uses separate Container Apps for publishing and subscribing. RBAC is assigned per managed identity and scoped to the Service Bus namespace.

Current behavior:

- The API Container App identity receives Azure Service Bus Data Sender for Dapr pub/sub publishing.
- The worker Container App identity receives Azure Service Bus Data Receiver for the Dapr pub/sub subscription.
- The worker KEDA scaler continues to use a separate least-privilege Listen connection string for scale decisions only.

19. Dapr managed identity metadata should be verified — addressed
The Dapr Azure components now make the identity choice explicit.

Current behavior:

- Dapr components continue to use managed identity and do not use account keys or Service Bus connection strings for runtime pub/sub/state access.
- `daprAzureClientId` is exposed in main.bicep and compute.bicep.
- Leave `daprAzureClientId` empty for system-assigned Container App identities, which is the current deployment model.
- Set `daprAzureClientId` only when switching the Dapr components to a user-assigned managed identity; compute.bicep then emits `azureClientId` metadata for Key Vault, Table Storage state stores, and Service Bus pub/sub.

Relevant locations: main.bicep, compute.bicep

20. Key Vault network ACLs are open — addressed with explicit deployment posture
Key Vault remains network-open by default because Container Apps/Dapr access through private networking is not configured in this Bicep yet. RBAC still restricts secret access to the API and worker managed identities.

Current behavior:

- `keyVaultNetworkDefaultAction` is exposed in main.bicep and dapr-infra.bicep.
- The default remains `Allow` to avoid breaking Container Apps/Dapr Key Vault access in the current public-ingress deployment.
- Production deployments that add VNet integration/private endpoints can set `keyVaultNetworkDefaultAction = 'Deny'`.

Relevant locations: main.bicep, main.bicepparam, dapr-infra.bicep

21. Redis is provisioned but not used by the design — addressed
Redis has been removed from the Bicep infrastructure because the design and implementation use Azure Table Storage-backed Dapr state stores.

Current behavior:

- dapr-infra.bicep provisions Service Bus and Key Vault only.
- compute.bicep defines the manifest and device registry state stores using `state.azure.tablestorage`.
- The unused Redis output and local docker-compose Redis volume were removed.

P2 implementation gaps and PRD readiness

Licensing/customer management is intentionally out of scope for this service and is assumed to be handled externally. The API still needs to enforce device ownership and revocation status, but it does not need to own subscriptions, billing, customer hierarchies, or license assignment for PRD.

Required for PRD

Client productionization

- Durable resume model for partially uploaded or partially committed runs. The client needs persisted pending upload/commit state so a process restart, PC reboot, or network loss does not strand local state behind server state.
- Windows service or scheduled task integration. A PRD backup client must run unattended after install, survive reboots, and expose predictable logs/status.
- VSS/shadow-copy strategy for locked and actively written files. Current direct reads can skip locked files; PRD needs a deterministic policy for snapshotting, retrying, or explicitly reporting skipped files.
- Active/locked file read behavior. `FileShare.Read` can still miss files being written or locked; PRD needs either VSS-based reads or clear skipped-file reporting that affects backup health.
- Remove or integrate `BackupDeltaComputer`. It is registered but appears unused by the current scan/upload path. Dead delta code should not remain in a PRD client.
- Restore/decryption for `ClientAndServer` encryption. Backup upload encryption is implemented, but PRD cannot offer encrypted backups without a tested way to restore using the recovery phrase.

Restore and operations

- Restore/download flow. PRD needs at least list current files for a device, select files, mint download SAS URLs, download, decrypt when needed, and verify plaintext hashes.
- Queryable state/index model for restore/list/status. The current key/value state model is insufficient for efficient listing and restore UX.
- Implement `GetAllFileEntriesAsync()` or replace it with explicit indexed query APIs. The current stub blocks restore/list workflows.
- Formal Azure Table partition/row key strategy. PRD needs defined partitioning for device file listings, file versions, commit jobs, and future operational queries.
- Poison-message reporting and operational endpoint. Failed commit messages need visibility and a safe operator workflow.
- Reconciliation/repair job. PRD needs a way to compare state, staged blobs, committed blobs, retired blobs, and commit progress records after partial failures.
- Stale run/staging cleanup. Lifecycle policy helps eventually, but PRD needs explicit cleanup/reporting for abandoned runs and old staging prefixes.
- Dapr component readiness checks. Sidecar health alone is not enough; readiness should verify state-store and pub/sub operations before accepting backup traffic.

Manifest hardening

- Manifest schema validation and version enforcement. `schemaVersion` exists, but the worker must reject unsupported versions and enforce required fields.
- Server-side file-count and manifest-size limits. PRD must bound manifest memory, processing time, and abuse potential.
- Exact manifest JSON schema documentation. The schema should include file identity, logical path, hashes, sizes, timestamps, deletion entries, and encryption metadata.
- Encryption metadata validation. `ClientAndServer` entries should require coherent algorithm/KDF/wrapped-key metadata before commit state is accepted.
- Optional manifest hash/signature. Not mandatory for first PRD if SAS and server-side validation remain strong, but useful later for tamper evidence.

Infrastructure and deployment

- Local Bicep validation tooling. PRD should have a documented local validation path in addition to CI.
- Key Vault private networking plan. `keyVaultNetworkDefaultAction` is configurable, but actual private endpoint/VNet integration is still not implemented.
- Audit stale Terraform references in architecture/deployment docs. The project uses Bicep; generic ignore examples for Terraform state files may remain.
- Production deployment validation. Azure CLI/Bicep validation has not been run in this environment, so PRD needs validation in an Azure-capable environment.

Not required for PRD in this service

- Customer/licensing model. Out of scope and managed externally.
- Full customer hierarchy or billing state. Out of scope.
- Rich admin portal. Useful later, but not required if operator endpoints/logs cover PRD operations.
- Per-device quota/policy inside this API. Only required if not enforced by the external customer/licensing system or storage-level controls.

Already addressed or no longer a PRD blocker

- Client/API staging protocol, run manifest upload, deletion-only commits, commit-status polling, and local-state-after-commit are implemented.
- Server-side staged blob size/hash validation is implemented.
- Commit idempotency, atomic claim, and per-file commit progress are implemented.
- API/worker split is implemented.
- Device ownership enforcement for backup routes is implemented.
- Backup upload encryption is implemented for `ClientAndServer` mode; only restore/decryption remains.
Summary
The project has a good architecture direction and a stronger codebase foundation than the initial review. The client/API integration now follows the target-aware staging/manifest/commit design, server-side validation and idempotent commit processing are in place, device ownership is enforced, and optional zero-knowledge backup encryption exists for uploads.

Current state: solid alpha.
Production readiness: not ready yet; restore, operations, reconciliation, SaaS administration, durable resume, and production deployment validation remain the main gaps.