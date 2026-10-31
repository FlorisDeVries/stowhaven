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

Each response distinguishes fresh observations, recorded terminal results, stale heartbeats, confirmed interruption, and unavailable observations. Execution ownership is probed separately against the actual database inode without creating or initializing backup state. An occupied lock protects against duplicate starts even when reporting is stale or unreadable. The recorded execution's kernel process start identity and current inode-lock owner must match before activity is attributed to that attempt; a PID alone can be reused. Missing or unverifiable ownership information never proves interruption or completion. An ended execution without a recorded final result is interrupted, retaining its last counters and run evidence as historical information.

While the companion remains running, malformed, missing, unreadable or unsupported reporting retains its last validated snapshot with an explicit unavailable/compatibility notice. Unreachable companion requests retain the browser's already displayed evidence with a warning. These retained details never establish current activity or new success. Restart reads durable supported reporting afresh and requires browser reauthorization; a stopped companion cannot maintain an in-memory copy of a reporting file that is subsequently lost. Unsupported schemas and incomplete staging files are preserved rather than interpreted or promoted.

Use **Open recent logs** during or after an attempt to inspect additional diagnostic context, and **Refresh recent logs** to fetch newer entries. This protected read uses the same bearer session and expiry as status. It reads at most 32 KiB from each of the two newest daily `backup-client-YYYYMMDD.log` files, returning at most 200 complete entries and 32,768 text characters. Older, oversized, partial and unrecognizable entries are omitted and the view indicates truncation. Whole entries containing credential, URL, SAS, recovery or other recognized secret material are replaced with a redaction marker, including multiline values. The companion creates no raw or sanitized log cache. The client's daily rotation and fourteen-file retention remain unchanged.

The default source is the client's existing `backup-client/logs` directory under LocalApplicationData. `Dashboard.LogDirectory` is a local configuration option for isolated deployments/tests, and must identify trusted, user-owned client log storage. HTTP requests cannot select paths, sources or larger limits. Symlinks, hardlink aliases, nonregular files and unsafe writable parent directories are rejected. Missing logs suggest running an attempt; empty/incomplete logs suggest retrying; rotation races suggest reopening; unreadable logs suggest checking permissions. The companion never creates or changes the log directory or source permissions.

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
  BackupClient__Dashboard__LogDirectory="$demo_root/client-logs" \
  "$demo_root/client/FlorisDeV.BackupClient" dashboard
```

In a second terminal, set `demo_root` to the directory printed by `echo "$demo_root"` in the first terminal and run:

```bash
env -i PATH="$PATH" DOTNET_ENVIRONMENT=Production \
  BackupClient__Dashboard__ReportingDirectory="$demo_root/reporting" \
  BackupClient__Dashboard__LogDirectory="$demo_root/client-logs" \
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

To reproduce opening redacted logs while an attempt is active and again after a completed check, run:

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj -m:1 \
  --filter FullyQualifiedName~Dashboard_RecentRedactedLogsCanBeOpenedDuringActiveAndCompletedAttempt
```

This uses real public backup orchestration, temporary SQLite/reporting storage, a controlled API wait and synthetic log fixtures. It reads the dashboard shell and authorized recent-log endpoint during that wait, then refreshes after the attempt ends. The synthetic bearer value never appears in either response. It does not launch a backup executable or inspect existing client logs. The separate `DashboardLogTests` cover bounds, secret formats, incomplete read boundaries, source restrictions, rotation, missing/unreadable logs and browser authorization.

A client can exit normally while the server is processing a run. The view records **pending** with the client inactive. The next scheduled or manually started backup attempt reconciles the pending run and then checks current files, possibly creating another run in that same attempt. The companion only observes this work. Uploaded files alone never establish success: both server completion and atomic local completion are required.

Original scan time, selection fingerprint, and skipped/deferred/failed counts survive pending-header updates and retries. A resumed degraded run and a newer clean run retain independent results; their attempt is conservatively reported as completed with errors. The current scan also retains its own evidence, including a clean no-change check after reconciliation. Legacy pending records without sufficient evidence remain unknown. A process death after atomic local completion but before optional reporting can likewise leave cleanliness unknown after restart; the client does not infer cleanliness from a recorded server backup or its timestamp. Reporting failures cannot roll back local completion.

To demonstrate pending exit followed by later reconciliation, distinct attempt/run relationships, server/local completion in authorized browser HTTP, and the active server-wait view, run:

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj -m:1 \
  --filter 'FullyQualifiedName~PendingRun_ExitsInactive|FullyQualifiedName~Dashboard_ReportsPendingRunWhile|FullyQualifiedName~PendingEvidence_Preserves'
```

These public-orchestration tests use real temporary SQLite/reporting files, synthetic targets, a controlled server boundary, and the real authorized companion interface. They do not launch the configured backup executable. Run records describe the attempt and the runs it handled. Freshness evidence is retained independently of attempt-history limits.

## Browsing recent attempts and file problems

Open **Recent backup attempts → Open or refresh history**, then expand an attempt
to inspect its run identities, original scans, server/local outcomes and file
problems. The authorized `/api/history` view retains the newest 100 attempts
across client and companion restarts. Each attempt keeps its own observations;
later reconciliation does not overwrite an earlier degraded result. An older
active observation means its final outcome is unknown; it does not establish that
the client is still running. Live ownership and interruption are shown separately
in the current observation.

File problems have timestamps, target names, relative paths and fixed retry or
investigation guidance. Categories distinguish unreadable/hash-skipped files,
files changed after hashing, deferred uploads, upload failures and server-rejected
files. Exception text, storage URLs, credentials and absolute paths never enter
this reporting contract. Sensitive-looking names are redacted before persistence;
server paths must pass identity, paging and count checks before becoming samples.

At most 100 diagnostic samples are retained per attempt. Complete category counts
remain available, and **Diagnostic details are incomplete** explicitly identifies
truncated or unavailable detail. A validated server rejection total is retained
even when the detailed response cannot be validated. Upload implementations that
provide only a failure count likewise leave detail explicitly incomplete.

Rolling `history.json` contains only portable allow-listed observations. Pruning
never touches SQLite backup recovery state or the independent `clean-evidence.json`
baseline. Pending runs retain their original identity, scan and degradation
evidence after their originating attempt ages out. Lists of handling attempts
are bounded to 100; each run keeps its original attempt identity separately.
History is updated at attempt boundaries and completion, not for each heartbeat.

To demonstrate more than 100 attempts, more than 100 file problems, restart
persistence, pending reconciliation after pruning and retained clean evidence:

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj -m:1 \
  --filter 'FullyQualifiedName~Dashboard_History|FullyQualifiedName~Dashboard_PruningRetains|FullyQualifiedName~Dashboard_DiagnosticNames|FullyQualifiedName~ObservationsRecoverAfterCompanionRestart'
```

The demonstration uses fresh synthetic temporary directories and controlled
server/file-access boundaries. Its process-kill case starts only the dedicated
test fixture; it never invokes an installed client or existing backup service.

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

Validated server completion also remains visible when local completion is still
pending. The dashboard labels that distinction explicitly; it does not establish
a successful backup or a clean result. A server-provided completion timestamp is
shown when available, separately from when the client first confirmed completion
and when it recorded local completion. A missing server timestamp stays unknown.

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

`ObservationsRecoverAfterCompanionRestartAndDistinguishSilenceFromConfirmedClientDeath`
also authorizes a polling HTTP browser equivalent, observes a stale heartbeat
while the real process still owns execution, rejects a duplicate start, stops
and restarts the real companion, reconnects to the same recorded attempt, then
forcibly kills the client and observes interruption. A subsequent real test
process acquires execution and records a clean check. Additional cases keep a
historical process alive while a different process owns execution, simulate PID
reuse, lose reporting files, and encounter incompatible schemas. Polling a
missing database verifies that observation creates no backup state.

A genuine CLI/service/browser smoke against isolated published binaries and
unique real user units remains part of the final installation verification.
Do not run a backup smoke using existing binaries with environment-only overrides:
development user secrets can override them. Use a reviewed fresh publication
with a unique UserSecretsId, scrubbed environment, explicit temporary paths and
synthetic local services. Never start or change the installed backup units as a
development demonstration.

## Actual scheduling information

The **Scheduling** section reads the configured systemd user timer on each
successful authorized status poll, including when there is no backup reporting.
`BackupClient:Dashboard:BackupTimerUnit` defaults to `backup-client.timer`; set it
and `BackupServiceUnit` together for custom installations. The timer's `Unit=`
must target that service, which must use `Type=oneshot` and `RemainAfterExit=no`.
Leave `BackupClient:Schedule:Enabled=false`; the alternative resident scheduler
is unsupported. The companion explains incompatible settings without changing
them.

Enabled state comes from actual `UnitFileState`, separately from current timer
activity. A disabled timer can still be active and have a next activation;
an enabled but inactive timer needs inspection. Missing units, masked timers,
unavailable user sessions, denied reads and unknown enablement (including
transient/static timers) have distinct messages. Install missing units for the
same user, inspect their configuration, then reload the user manager. Review
whether scheduling is intended before manually enabling, starting or unmasking
a timer. The dashboard performs none of those actions while reading status.

Next activation comes only from `NextElapseUSecRealtime` returned by
`systemctl --user show --timestamp=unix`. This machine-readable timestamp format
requires systemd 251 or newer; older/unsupported command output produces
unavailable information with upgrade guidance. Absent, zero, infinite or
unrecognized values remain **Unknown**; configured calendars and resident
intervals never substitute an invented next time. Timer activation is scheduling
information and does not establish a clean backup outcome, completed server
backup, or freshness.

Run the authorized HTTP demonstration and isolated command-fixture cases with:

```bash
dotnet test tests/services/client/FlorisDeV.BackupClient.Tests.csproj --no-restore -m:1 \
  --filter FullyQualifiedName~DashboardSchedulingTests
```

The subprocess cases use temporary synthetic unit names and an explicitly
nonexistent user bus as fallback. They verify machine-readable next activation,
unknown values, masked units, and an actual `systemctl` unavailable-session read;
no case starts a backup or changes installed units. Actual installation and
activation verification uses the isolated smoke procedure described above.
