# Local dashboard development preview

The optional Linux dashboard is a separate, persistent observer. It reads private client reporting and remains available between backup invocations. An authorized operator can request the existing systemd user backup service with **Start backup**. The companion does not execute backup work itself or change your service/timer. The live view shows API waking, scanning, uploading, file counts, successful transfer bytes and average transfer rates. Scanning and uploading can appear together. Attempt outcomes distinguish no changes, pending, succeeded, completed with errors, and failed. Each server-backed run shows its own identity, originating/handling attempts, commit identity, server processing, local completion, original scan time, and cleanliness.

For an existing configured client, add this section alongside `BackupTargets` in `appsettings.local.json` next to the executable:

```json
{
  "BackupClient": {
    "Dashboard": {
      "Enabled": true,
      "ReportingDirectory": "/absolute/private/path/to/reporting",
      "Port": 0
    }
  }
}
```

Keep your existing settings when adding it. Use the same absolute reporting directory for the backup command and companion. The default is `~/.local/share/backup-client/reporting` (or the current user's LocalApplicationData equivalent). Reporting is disabled by default; enabling it records observations even when the companion is stopped. It is separate from the credential-bearing SQLite state. The directory and files must belong to your account, with directory mode 0700 and file mode 0600. Symlinks and unsafe writable parent directories are rejected. Use a directory on a local filesystem supporting atomic rename and Unix permissions.

Normal scheduled attempts record observations after reporting is enabled. A clean no-changes attempt records a clean check. To open the existing reporting in a configured installation, run the observer commands only:

```bash
./FlorisDeV.BackupClient dashboard
```

In another terminal, using the same executable and configuration:

```bash
./FlorisDeV.BackupClient open-dashboard
```

The first command stays running. The second opens an authorized browser tab. A headless desktop can use `open-dashboard --print-url` and paste the short-lived URL into a browser on the same machine within one minute. Treat that URL as a secret; do not save it in logs or share it. Opening a plain companion address shows a public shell and requires `open-dashboard` for private observations.

The browser polls once per second. The client coalesces live observations to at most one per second and keeps sending heartbeats during quiet work; the final outcome is recorded immediately after stopping the heartbeat. After 15 seconds without a heartbeat the view labels the observation stale, without declaring the process stopped. Streaming scans and transfers have unknown totals, so the view does not invent percentages or completion estimates. Reloading the same tab preserves its origin-scoped session; sessions are not cookies and are never sent to other local ports. Authorization ends after companion restart or 12 hours without keyboard/pointer activity. Passive polling does not extend it. Run `open-dashboard` again to authorize. Closing the browser or stopping the companion with Ctrl+C leaves backup execution alone.

For a reproducible clean-check demonstration, run the public backup orchestration integration test below. It uses the real scanner and temporary SQLite state with a controlled remote API dependency, records a clean check while the companion is stopped, starts the companion, authorizes a browser HTTP client, and reads the same recorded attempt. It does not contact your configured API or read user secrets:

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj \
  --filter FullyQualifiedName~Dashboard_CleanCheckSurvivesStoppedCompanionWithoutLeakingState
```

To demonstrate the executable observer and browser authorization independently, use a fresh private temporary directory:

```bash
demo_root=$(mktemp -d)
chmod 700 "$demo_root"
dotnet publish src/services/client/FlorisDeV.BackupClient.csproj -c Release \
  -p:UserSecretsId="stowhaven-dashboard-demo-$(python3 -c 'import uuid; print(uuid.uuid4().hex)')" \
  -o "$demo_root/client"

env -i PATH="$PATH" DOTNET_ENVIRONMENT=Production \
  BackupClient__Dashboard__ReportingDirectory="$demo_root/reporting" \
  "$demo_root/client/FlorisDeV.BackupClient" dashboard
```

In a second terminal, set `demo_root` to the directory printed by `echo "$demo_root"` in the first terminal and run:

```bash
env -i PATH="$PATH" DOTNET_ENVIRONMENT=Production \
  BackupClient__Dashboard__ReportingDirectory="$demo_root/reporting" \
  "$demo_root/client/FlorisDeV.BackupClient" open-dashboard --print-url
```

Paste the URL into a local browser within one minute. This fresh observer shows **No backup attempt has been recorded.** The automated clean-check demonstration above separately proves the genuine reporting-to-authorized-browser path, including the prominent **No backup has been created yet** message for an empty first check. Installation and a real installed-client backup smoke are delivered in later slices. Only the observer commands are run against this fresh publication; do not invoke a no-argument client as part of this demo. Stop the observer with Ctrl+C and remove the private demo directory when finished.

The focused automated checks are:

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj --filter FullyQualifiedName~Dashboard
```

Reporting failures do not fail backups. If observations are unavailable, check the companion is running under the same account, the absolute directory matches, and private permissions/parents are safe. Unsupported schema versions show explicit upgrade guidance and are preserved by both reader and producer. Upgrade client and companion together; preserve reporting data when rolling back. Older readers will refuse newer schemas. Do not copy backup state or credentials into reporting files. This preview does not install a service; installation is delivered separately.

For a controlled long-work demonstration of the real public backup orchestration, use:

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj -m:1 \
  --filter 'FullyQualifiedName~Dashboard_OverlappingScanUpload|FullyQualifiedName~Dashboard_QuietApiWait'
```

These tests scan synthetic files with real temporary SQLite/reporting storage and controlled scanner, uploader, API and time boundaries. They pause a batch upload while scanning remains active, verify counters and exact successful transfer bytes after retries, hold quiet hashing/API work across heartbeats, and reconnect authorized HTTP clients after a page-equivalent reload and companion restart. They never launch the configured backup executable. A fresh-published, isolated real-client/systemd backup demonstration remains part of the installation/recovery smoke slice; do not use existing binaries, state or environment-only overrides for that check.

A client can exit normally while the server is processing a run. The view records **pending** with the client inactive. The next scheduled or manually started backup attempt reconciles the pending run and then checks current files, possibly creating another run in that same attempt. The companion only observes this work. Uploaded files alone never establish success: both server completion and atomic local completion are required.

Original scan time, selection fingerprint, and skipped/deferred/failed counts survive pending-header updates and retries. A resumed degraded run and a newer clean run retain independent results; their attempt is conservatively reported as completed with errors. The current scan also retains its own evidence, including a clean no-change check after reconciliation. Legacy pending records without sufficient evidence remain unknown. A process death after atomic local completion but before optional reporting can likewise leave cleanliness unknown after restart; the client does not infer cleanliness from a recorded server backup or its timestamp. Reporting failures cannot roll back local completion.

To demonstrate pending exit followed by later reconciliation, distinct attempt/run relationships, server/local completion in authorized browser HTTP, and the active server-wait view, run:

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj -m:1 \
  --filter 'FullyQualifiedName~PendingRun_ExitsInactive|FullyQualifiedName~Dashboard_ReportsPendingRunWhile|FullyQualifiedName~PendingEvidence_Preserves'
```

These public-orchestration tests use real temporary SQLite/reporting files, synthetic targets, a controlled server boundary, and the real authorized companion interface. They do not launch the configured backup executable. Run records describe the attempt and the runs it handled. Freshness evidence is retained independently of attempt-history limits.

## Backup freshness

Set `BackupClient:Dashboard:MaximumBackupAge` explicitly, for example
`"1.00:00:00"` for one day. Its default is `null`: the dashboard shows the age
without a fresh/overdue judgment. Only a positive duration enables a judgment.
Timer activity and old success timestamps cannot establish freshness.

The latest attempt, last clean check and last completed server backup are
separate observations. A failed attempt stays visible alongside an earlier
clean result. A clean check with no changes can refresh the clean baseline,
including an empty first scan, while prominently retaining “No completed server
backup exists yet.” A check never establishes that a restore test happened.
Completed backups with errors remain visible as completed backups and do not
refresh the clean baseline. Unintended skipped, deferred or failed files prevent
a clean result; explicitly excluded files do not.

Age starts at the original scan time, with completion recorded separately.
Delayed reconciliation does not redate that scan. A clean new run can establish
freshness even when the same attempt also reconciles older degraded work and
therefore ends with errors. A completed current clean no-changes scan likewise
stands on its own evidence. Missing legacy scan/cleanliness evidence remains
unknown.

The private, versioned `clean-evidence.json` keeps the latest clean result, clean
check and completed server backup separately from rolling attempt observations.
It contains only IDs, scan/completion times, selection hashes and outcome labels;
no credentials or target paths. Optional reporting failures never fail backups.
Unknown schema versions are preserved for a matching client/companion upgrade.

The companion recomputes the effective selection on each status request, reading
target-level ignore rules with the same override behavior as the scanner.
`appsettings.json` and `appsettings.local.json` reload while it runs. Target or
effective exclusion changes preserve historical evidence and show a
configuration-changed explanation until a clean result covers the selection.
Environment and command-line configuration changes take effect after restarting
the companion. If current configuration cannot be read, it cannot judge the
current selection as fresh.

The deterministic freshness demonstration uses controlled time, actual temporary
files (including locked and explicitly excluded files), the public backup
orchestrator and authorized companion HTTP:

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj --no-restore -m:1 \
  --filter FullyQualifiedName~Dashboard_Freshness
```

It covers a fresh clean check, overdue observations, later failed attempts with
retained clean evidence, live effective-selection edits, delayed completion,
degraded old work with a clean new run and honest legacy uncertainty. The browser
renders these same separate evidence fields and scan/completion times.

## Starting a backup safely

The companion and backup service must load the same `Database:FilePath` and
`BackupClient:Dashboard:ReportingDirectory`, and reporting must be enabled in the
backup client. Use an absolute database path so different working directories do
not select different state. `BackupClient:Dashboard:BackupServiceUnit` defaults to
`backup-client.service`; a custom installation can set its own `.service` unit.
The supported arrangement is a `Type=oneshot` systemd user service and timer,
with `BackupClient:Schedule:Enabled=false`.

**Start backup** requests `systemctl --user start --no-block --job-mode=fail`.
“Execution confirmed” means the client acquired the shared state-file guard and
recorded a new attempt for that systemd invocation. An attempt that finishes
quickly is also confirmed. Acceptance by systemd alone is insufficient: if
execution cannot be confirmed, check the service journal, matching configuration
and private reporting permissions before retrying. The browser shows explicit
guidance for missing units, unavailable user sessions, denied activation and
incompatible resident scheduling. No error falls back to directly launching the
backup client.

Every `RunAsync` invocation, including CLI and timer execution, must acquire
whole-attempt ownership before reading or mutating backup state. On Linux this
uses native `flock` on the actual database inode, recognizing hard links, leaf
and parent-directory symlinks and relative aliases. SQLite connection closure
does not release ownership. A separate open-file-description byte lock reserves
start requests without preventing the child from acquiring execution ownership.
Duplicates return immediately and queue no follow-up work. Owner exit or process
death releases both guards; do not delete a database or lock file to “unlock” it.
Keep backup state on a local filesystem; NFS/SMB state storage is unsupported.

## Reproducible contention and recovery demonstration

Run the dedicated test process fixture through the public orchestration and
control tests; it creates only temporary synthetic targets, SQLite state and
private reporting. It does not load client configuration, user secrets or the
installed services:

```bash
dotnet restore tests/services/client/FlorisDeV.BackupClient.Tests.csproj -m:1
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj --no-restore -m:1 \
  --filter FullyQualifiedName~BackupExecutionProcessTests
```

The demonstration holds one real process after SQLite has opened/closed its
connections, competes with a second process through each path alias, verifies
immediate rejection, forcibly kills the owner, then confirms a new process can
finish a clean check with recoverable state. Separate requesting processes
compete for the same inode reservation, with no delayed backup after the winning
attempt finishes. Authorized HTTP cases exercise active and fast completed
execution confirmation, errors, browser expiry and concurrent browser starts.
The systemd boundary is controlled in these deterministic tests.

A genuine CLI/service/browser smoke against isolated published binaries and
unique real user units remains part of the final installation verification.
Do not run a backup smoke using existing binaries with environment-only overrides:
development user secrets can override them. Use a reviewed fresh publication
with a unique UserSecretsId, scrubbed environment, explicit temporary paths and
synthetic local services. Never start or change the installed backup units as a
development demonstration.
