# Local dashboard development preview

The optional Linux dashboard is a separate, persistent observer. It reads private client reporting and remains available between backup invocations. It does not start backups or change your service/timer. The live view shows API waking, scanning, uploading, file counts, successful transfer bytes and average transfer rates. Scanning and uploading can appear together. Other attempt outcomes are named without claiming a clean result.

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
