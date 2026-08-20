# Stowhaven Client Configuration Guide

This guide covers first-time setup, backup targets, scheduling, resume behavior, and restore configuration for the .NET Stowhaven client.

## Quick start

### Installed client

Run the interactive setup once from the published/installed client directory:

```bash
backup-client configure
```

On Windows, where the executable may not be linked as `backup-client`:

```powershell
.\FlorisDeV.BackupClient.exe configure
```

The command:

1. Suggests common folders that exist on the machine.
2. Validates the targets you select or enter.
3. Writes machine-specific target overrides to `appsettings.local.json` beside the executable.
4. Opens the browser for Entra ID sign-in when necessary.
5. Registers the local device and verifies end-to-end API access.

Normal backup and restore runs never open a browser. If silent authentication can no longer refresh a token, run `backup-client login` explicitly.

Useful setup variants:

```bash
backup-client configure --skip-targets
backup-client configure --skip-login --skip-access-check
backup-client login
```

### Source checkout

With the local Docker Compose stack running:

```bash
dotnet run --project src/services/client -- configure --skip-login
dotnet run --project src/services/client
```

The Development configuration points at the loopback API and uses the development no-op credential, so Entra sign-in is skipped for local API calls.

## Configuration files and precedence

The client resolves configuration relative to the executable directory, not the shell's current directory. Standard .NET configuration precedence applies, with `appsettings.local.json` loaded after the normal appsettings files.

| File | Purpose |
| --- | --- |
| `appsettings.json` | Shipped placeholders for the hosted Gateway and authentication identifiers, plus the default ignore file and baseline client options. Replace the placeholders for your deployment before publishing the client. |
| `appsettings.{Environment}.json` | Environment-specific overrides. |
| `appsettings.local.json` | Per-machine backup targets written by `configure`; intentionally excluded from publish output. |

You can edit `appsettings.local.json` manually, but the `configure` command is preferred because it validates target paths and preserves them across client upgrades.

Example machine-specific file:

```json
{
  "BackupClient": {
    "BackupTargets": {
      "documents": "C:\\Users\\YourName\\Documents",
      "projects": "D:\\Projects"
    }
  }
}
```

Linux example:

```json
{
  "BackupClient": {
    "BackupTargets": {
      "home": "/home/yourname",
      "photos": "/mnt/photos"
    }
  }
}
```

## What gets backed up

Only directories listed in `BackupClient:BackupTargets` are scanned. The shipped target list is empty; common folders are suggestions offered by `configure`, not automatic inclusions.

Each target name becomes the first segment of its logical path. For example, target `documents` and relative file `tax/2025.pdf` become `documents/tax/2025.pdf`. Target names cannot contain `/` or `\`.

The shipped `.backupignore` excludes common build outputs, dependencies, caches, logs, temporary files, version-control metadata, and operating-system files. Review those defaults before using the client for server or disaster-recovery data. See the [.backupignore reference](BACKUPIGNORE.md).

## Ignore-file selection

`BackupClient:IgnoreFilePath` selects the global ignore file. The shipped default is `.backupignore`, resolved beside the executable.

For each target:

1. If `{target-root}/.backupignore` exists, that file is used for the target.
2. Otherwise, the configured global ignore file is used.

The target file replaces the global patterns; the two files are not merged. Nested `.backupignore` files are not discovered.

## Running a backup

Run the executable without a command:

```bash
backup-client
```

The client registers its device if needed, scans all targets, uploads changed/new files directly to Blob Storage, submits deletions in `run-manifest.json`, commits the run, and polls the asynchronous worker.

Committed blobs live under `devices/{deviceId}/files/`. Staged blobs under `staging/{deviceId}/{runId}/` and the temporary run manifest may be removed after a successful commit, so they should not be used as the final verification location.

## Scheduling

### Linux

The bundled `install.sh` creates and enables a systemd user timer. By default it runs daily; override the time while installing:

```bash
BACKUP_CLIENT_SCHEDULE_TIME=03:30:00 ./install.sh
```

The installer keeps everything user-owned under `~/.local/share/backup-client`, links the executable through `~/.local/bin`, and preserves `appsettings.local.json` during upgrades. It also explains how to enable the timer manually when a systemd user session is unavailable.

Because the Linux MSAL cache uses libsecret and a D-Bus session, prefer the systemd user timer over cron. Enabling linger lets the user timer run while logged out:

```bash
loginctl enable-linger "$USER"
```

### Windows Task Scheduler

Leave `BackupClient:Schedule:Enabled` as `false` and schedule the executable as a one-shot task:

```powershell
schtasks /Create /TN "BackupClient Daily" /TR "C:\Tools\BackupClient\FlorisDeV.BackupClient.exe" /SC DAILY /ST 02:00 /RU "%USERNAME%" /RL LIMITED
```

Use the same Windows account that ran `configure`, because the token cache is protected for that user.

### Long-running Windows service mode

The executable also supports Windows service hosting:

```json
{
  "BackupClient": {
    "Schedule": {
      "Enabled": true,
      "RunOnStartup": true,
      "IntervalMinutes": 1440
    }
  }
}
```

When scheduling externally, keep `Schedule:Enabled` set to `false` so one invocation performs one backup and exits.

## Interrupted-run resume

The client stores a pending-run journal in its local SQLite database.

- Files already staged with the same logical path, hash, and size are reused.
- SAS URLs are refreshed for an existing run when they may expire before the next batch completes.
- Manifest upload and `commit-run` are idempotent.
- A client-side commit polling timeout leaves the durable server-side commit in progress and preserves the journal for the next invocation.
- Local successful-backup state is updated only after the server reports `Succeeded` or `CompletedWithErrors`.

If an old run can no longer be resumed safely, its uncommitted blobs remain isolated under its run ID and are eligible for scheduled staging cleanup.

## Locked files

The client does not take VSS/shadow-copy snapshots.

```json
{
  "BackupClient": {
    "LockedFilePolicy": "SkipLocked"
  }
}
```

`SkipLocked` is the default. It opens files with read sharing and skips files that cannot be read consistently.

```json
{
  "BackupClient": {
    "LockedFilePolicy": "ReadThroughSharedWrites"
  }
}
```

`ReadThroughSharedWrites` permits read/write/delete sharing. It can capture a file while another application modifies it and is not equivalent to a snapshot.

## Restore

Configure a destination and optional source device/logical paths:

```json
{
  "BackupClient": {
    "Restore": {
      "DeviceId": null,
      "DestinationPath": "C:\\Restore",
      "LogicalPaths": [],
      "ListPageSize": 500,
      "OverwriteExisting": false
    }
  }
}
```

Then run:

```bash
backup-client restore
```

- A null `DeviceId` uses the local device ID.
- An empty `LogicalPaths` array restores all active files returned by the API.
- Existing destination files are rejected unless `OverwriteExisting` is `true`.
- `ClientAndServer` backups require the local recovery phrase file. The client verifies ciphertext, HMAC, and plaintext integrity before writing the restored file.
- Archive-tier blobs must be rehydrated to an online tier before restore; the client does not automate rehydration.

## Essential configuration

| Property | Default | Description |
| --- | --- | --- |
| `BackupTargets` | empty | Named directories to scan; at least one is required for a backup run. |
| `IgnoreFilePath` | `.backupignore` in shipped config | Global ignore file, resolved relative to the executable when the path is relative. |
| `MaxParallelUploads` | `4` | Maximum concurrent uploads. |
| `StagingAccessTier` | `Hot` | Explicit tier for new staging blobs: `Hot`, `Cool`, or `Cold`. |
| `LargeFileThresholdBytes` | `10485760` | Size at which progress logging is enabled. |
| `BlobUploadTimeoutSeconds` | `600` | Timeout for one upload attempt. |
| `MaxFailurePercentage` | `5` | Maximum tolerated percentage of client upload failures. |
| `CommitStatusPollIntervalSeconds` | `2` | Delay between commit-status requests. |
| `CommitStatusTimeoutSeconds` | `600` | Time to poll before deferring completion to a later run. |
| `LockedFilePolicy` | `SkipLocked` | Locked-file read behavior. |
| `Schedule:Enabled` | `false` | Enables the client's long-running scheduler. |
| `Encryption:Mode` | `ServerSideOnly` | Selects server-only or additional client-side encryption. |

See [Advanced Configuration](ADVANCED_CONFIGURATION.md) for the complete reference.

## Local files

Unless explicitly overridden, client state is stored below the platform's local application-data directory:

| Data | Windows | Linux |
| --- | --- | --- |
| SQLite state | `%LOCALAPPDATA%\backup-client\backup-state.db` | `~/.local/share/backup-client/backup-state.db` |
| MSAL token cache | `%LOCALAPPDATA%\backup-client\backup-client.cache` | `~/.local/share/backup-client/backup-client.cache` |
| Recovery phrase | `%LOCALAPPDATA%\backup-client\recovery-phrase.json` | `~/.local/share/backup-client/recovery-phrase.json` |
| Logs | `%LOCALAPPDATA%\backup-client\logs\` | `~/.local/share/backup-client/logs/` |

Custom `Database:FilePath` and `Encryption:RecoveryPhraseFilePath` values override the corresponding defaults.

## Troubleshooting

### No backup targets configured

Run `backup-client configure` or add at least one entry to `BackupClient:BackupTargets` in `appsettings.local.json`.

### Authentication requires interaction

Operational runs are intentionally silent-only. Run `backup-client login` interactively, then retry the scheduled backup.

### Files are missing

Check the target-root `.backupignore` first. If it does not exist, check the global file beside the executable. Also review warnings for inaccessible, locked, deleted-during-scan, or broken-symlink files.

### Database is locked

Do not run multiple client instances against the same SQLite database. Ensure an external scheduler cannot overlap runs.

### Backup is slow

Review ignore rules before increasing concurrency. For many small files, a higher `MaxParallelUploads` may help; for slow links or large files, lower concurrency and a longer `BlobUploadTimeoutSeconds` are usually safer.

### Restore cannot read an archived blob

Rehydrate the blob to Hot or Cool in Azure Storage and retry after rehydration completes.
