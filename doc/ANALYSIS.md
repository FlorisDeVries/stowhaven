Verdict
The project is not production-ready yet outside CI/CD. The foundation is good: clean .NET structure, tests pass, Dapr/state/pubsub concepts are present, observability is considered, and the design is directionally sound. The client/API staging protocol, run manifest upload, deletion-only commits, and local-state-after-commit flow are now implemented, but authentication/device ownership, commit-worker separation, server-side validation/idempotency, and production infrastructure wiring remain blockers.

Validation run:

dotnet test [FlorisDeV.BackupApi.sln](http://_vscodecontentref_/0) --no-restore --verbosity minimal
Result: 395 tests passed, 0 failed
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
Calls commit with mandatory ManifestBlobPath.
Relevant locations:

Upload path uses uniqueFileId when present: FileUploader.cs
Commit uploads manifest and includes ManifestBlobPath: BackupService.cs
API reads manifest at runs/{deviceId}/{runId}/run-manifest.json: BackupProcessingService.cs
API moves staged source blob by uniqueFileId: BackupProcessingService.cs
Remaining risk: server-side validation should derive or strictly validate ManifestBlobPath instead of trusting the client-provided path.

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
8. Blob move fallback can cause early deletion fees and partial failures
The design relies on ADLS Gen2 rename to avoid early deletion penalties. The implementation catches all rename errors and silently falls back to copy+delete.

Relevant location:

Rename failure is swallowed and falls back to copy/delete: BlobStorageService.cs:238-267
Impact:

If HNS rename fails, copy+delete may trigger costs and leave inconsistent state.
Silent fallback hides a major production assumption violation.
Recommendation:

In production, fail hard if ADLS Gen2 rename fails unless an explicit AllowCopyDeleteFallback flag is enabled.
Log the rename exception with high severity.
Use leases or idempotency markers around moves.
9. Commit processing is not transactionally safe
ProcessFileEntryAsync moves the staged blob, saves a new FileVersion, retires the old version, then saves FileEntry.

Relevant location:

Multi-step commit flow: BackupProcessingService.cs:212-260
Impact: failure halfway through can leave:

blob moved but state not updated,
old version retired but new mapping not saved,
state says active but blob missing,
retries failing because source blob was already moved.
Recommendation:

Make commit idempotent per file.
Record per-file commit status.
Treat destination-exists/source-missing as recoverable when state confirms prior progress.
Use deterministic state transitions: Pending → Moved → StateUpdated → Succeeded.
Add repair/reconciliation tooling.
10. Duplicate commits are not deduplicated by deviceId + runId
CreateCommitJobAsync always creates a new CommitJob with a new GUID.

Relevant location:

New commit ID every time: StateStoreManager.cs:183-216
Impact: retrying /commit-run for the same runId can enqueue multiple jobs for the same backup run.

Recommendation: key commit jobs by deviceId/runId or maintain a secondary idempotency key.

11. Concurrency handling is incomplete
The code checks CommitJobStatus.Processing and returns, but it does not atomically claim the job. Two consumers can read Queued and both update to Processing.

Relevant location:

Non-atomic processing claim: BackupProcessingService.cs:35-67
Recommendation: use ETag-based compare-and-set for Queued → Processing, and if it fails, abandon/skip.

12. Uploads overwrite blobs
The client uses overwrite: true for smaller files and does not enforce create-only semantics.

Relevant location:

Overwrite upload: FileUploader.cs:151-153
This conflicts with the design’s intent to use If-None-Match: * and immutable unique blob names.

Recommendation: use BlobRequestConditions { IfNoneMatch = ETag.All } and unique file IDs.

P1 security issues
13. ManifestBlobPath is not validated
ManifestBlobPath is accepted from the client and later used to download a blob.

Relevant locations:

Request property has no validation: CommitBackupRunRequest.cs:6-17
API uses it directly: BackupRunService.cs:102-113
Worker downloads from that path: BackupProcessingService.cs:75-83
Recommendation: server should derive the manifest path from authenticated device/run only, or validate it strictly against:

runs/{deviceId:N}/{runId:N}/run-manifest.json

14. Forwarded headers trust all proxies
KnownIPNetworks and KnownProxies are cleared while all forwarded headers are accepted.

Relevant location:

Forwarded headers configuration: ProgramExtensions.cs:204-214
Impact: if exposed incorrectly, clients can spoof forwarded headers. This also affects RemoteIpAddress, which is used for SAS IP restriction.

Recommendation:

Configure known Azure proxy ranges or ACA ingress behavior carefully.
Avoid relying on client IP restriction unless tested with ACA.
Prefer short TTL, create-only SAS, and device/run-scoped paths.
15. SAS IP restriction may break real customer clients
StartBackupRun uses HttpContext.Connection.RemoteIpAddress.

Relevant location:

Client IP capture: BackupController.cs:35-40
In ACA, this may be a proxy/NAT address, not the actual customer IP. Residential customer IPs can also change mid-run.

Recommendation: make SAS IP restriction optional and configurable per deployment/customer.

16. Development anonymous auth is dangerous if environment is misconfigured
In development, authentication is fully bypassed.

Relevant location:

Anonymous auth in development: ProgramExtensions.cs:126-137
This is acceptable locally, but production deployment must ensure ASPNETCORE_ENVIRONMENT=Production always. Consider adding a startup guard that refuses anonymous auth unless explicitly enabled.

P1 infrastructure issues
17. Bicep still contains an unused API key model
The design is JWT/Entra-based, but Bicep still provisions apiKey.

Relevant locations:

Secure apiKey parameter: main.bicep:28-30
Container secret/env API_KEY: compute.bicep:139-143
No API-key authentication path appears to be used. Remove it unless there is a specific internal admin endpoint.

18. Service Bus roles are incomplete for a subscriber
The app publishes and subscribes to Service Bus. Bicep assigns Azure Service Bus Data Sender, but the same app also needs to receive messages.

Relevant location:

Sender-only role assignment: main.bicep:158-169
Recommendation: assign Azure Service Bus Data Receiver or Azure Service Bus Data Owner depending on Dapr component requirements.

19. Dapr managed identity metadata should be verified
The Dapr components define Azure Table Storage and Service Bus metadata, but no explicit managed identity metadata is configured.

Relevant locations:

Table state component: compute.bicep:60-78
Service Bus pub/sub component: compute.bicep:82-114
Key Vault secret store component: compute.bicep:44-57
Recommendation: verify current Dapr Azure component identity requirements for Container Apps and explicitly configure managed identity where required.

20. Key Vault network ACLs are open
Relevant location:

Key Vault defaultAction: 'Allow': dapr-infra.bicep:76-79
For production, tighten this if possible. At minimum, document why it remains open for ACA/Dapr.

21. Redis is provisioned but not used by the design
The design says the state store is Azure Table Storage via Dapr. Bicep provisions Redis as “Dapr State Store”, but compute uses Table Storage.

Relevant locations:

Redis provisioned: dapr-infra.bicep:23-37
Table Storage Dapr component used: compute.bicep:60-78
Recommendation: remove Redis unless it has a defined production purpose.

P2 implementation gaps
Client
Missing or incomplete for production:

No generation of uniqueFileId matching the API contract.
No manifest upload.
No commit-status polling.
No resume model for partially uploaded runs.
No encryption implementation, although design assumes client-side encryption.
No Windows service/scheduled task integration.
No VSS/shadow-copy strategy for locked files.
FileShare.Read may skip files being written or locked: FileSystemService.cs:300-308
BackupDeltaComputer appears unused and uses absolute paths inconsistently with TaggedFile.GetStoragePath().
API
Missing or incomplete for production:

No restore/download endpoint.
No customer model, device list endpoint, or device revocation endpoint.
No admin/operator APIs.
No per-device quota/policy.
No manifest schema versioning.
No server-side file-count/manifest-size limits.
No poison-message handling endpoint/reporting.
No reconciliation job.
No stale run/staging cleanup beyond storage lifecycle.
No explicit Dapr health/readiness checks for state/pubsub availability before accepting traffic.
State model
The current Dapr key/value state model can work for simple lookup, but production restore/status/listing will need queryable indexes. GetAllFileEntriesAsync() is explicitly a stub.

Relevant location:

Stubbed production note: StateStoreManager.cs:426-436
For Azure Table Storage, define the physical partition/row key strategy now.

Design review
The design is strong conceptually, especially:

direct client-to-Blob upload,
short-lived SAS,
async commit,
state store as authority,
immutable blob versions,
lifecycle-based cost management.
But it needs tightening before it can be the production baseline.

Design changes recommended
Separate “control manifest” from “file manifest” clearly

Define the exact JSON schema.
Include schemaVersion.
Include encryptionMetadata.
Include contentHash, size, mtime, relativePath, uniqueFileId.
Include manifest hash/signature if needed.
Make commit idempotency explicit

Define behavior for:
repeated /commit-run,
partially moved blobs,
missing staged source but existing destination,
duplicate uniqueFileId,
failed old-version retirement.
Define customer/device authorization

The current design mentions binding deviceId to authenticated user/tenant, but implementation and schema are missing.
This is mandatory for centralized customer machines.
Define restore flow

At minimum:
list current files for device,
request download SAS for selected paths,
restore latest version,
optionally restore deleted/retired versions within retention.
Clarify encryption

Design assumes client-side encryption but does not specify:
key source,
key rotation,
metadata format,
restore/decryption flow,
recovery if the client machine is lost.
Clarify infrastructure

Design mentions Terraform, project uses Bicep.
Decide and update docs.
Remove unused Redis/API-key resources.
Add explicit Service Bus receiver role and Dapr identity configuration.
Clarify storage lifecycle assumptions

The design assumes rename avoids early deletion fees.
Production code must fail if rename is unavailable instead of silently copy/deleting.
Recommended production-readiness roadmap
Phase 1: make the happy path actually work
Generate uniqueFileId in client.
Upload files to staging/{deviceId:N}/{runId:N}/{uniqueFileId}.
Upload runs/{deviceId:N}/{runId:N}/run-manifest.json.
Include deleted files in manifest.
Commit with server-derived manifest path.
Poll commit-status until terminal state.
Only then mark local state successful.
Phase 2: secure multi-customer operation
Extend the current device registration with licensing/customer binding.
Keep device registry state separate from manifest/run state.
Keep authorization checks on start-run, commit-run, and commit-status.
Validate all paths and manifest content.
Remove or isolate anonymous development auth.
Phase 3: harden commit processing
Split worker from API.
Add idempotent per-file state transitions.
Add size/hash validation.
Add duplicate commit deduplication.
Add poison queue observability.
Add reconciliation/repair job.
Phase 4: production infrastructure cleanup
Fix secret/config naming.
Add Service Bus receiver role.
Verify Dapr managed identity metadata.
Remove Redis if unused.
Remove API key if unused.
Tighten Key Vault/network posture.
Add Bicep validation in local tooling.
Phase 5: restore and operations
Implement restore/download flow.
Add admin/customer/device APIs.
Add metrics dashboards and alerts.
Add backup success SLOs.
Add run history and audit logs.
Add documented client installation/update strategy.
Summary
The project has a good architecture direction and a decent codebase foundation, but the current client/API integration does not yet satisfy the design. The most urgent gap is the missing run-manifest.json and uniqueFileId-based staging protocol. After that, device authorization, commit idempotency, server-side verification, and production Dapr/secret wiring are the next blockers.

Current state: good prototype / early alpha.
Production readiness: not ready yet.