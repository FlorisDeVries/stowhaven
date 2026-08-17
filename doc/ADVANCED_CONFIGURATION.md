# Advanced client configuration

This guide covers client settings that are normally left at their defaults. Start with the interactive setup in [Client configuration](CLIENT_CONFIGURATION.md); add overrides to `appsettings.local.json` only when a workload needs them.

## Configuration precedence

The client loads configuration in this order, with later sources taking precedence:

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. .NET user secrets in Development
4. environment variables
5. `appsettings.local.json`
6. command-line configuration values

`backup-client configure` writes machine-specific targets to `appsettings.local.json` next to the executable.

## Performance and staging

```json
{
  "BackupClient": {
    "MaxParallelUploads": 4,
    "StagingAccessTier": "Hot",
    "LargeFileThresholdBytes": 10485760,
    "BlobUploadTimeoutSeconds": 600
  }
}
```

- `MaxParallelUploads` limits concurrent blob uploads. Increase it gradually on fast links; reduce it when bandwidth, memory, or storage throttling is a concern.
- `StagingAccessTier` accepts `Hot`, `Cool`, or `Cold`. `Hot` is the default and is normally the safest choice because staged content is read during commit. `Archive` is rejected.
- `LargeFileThresholdBytes` controls when the client emits large-file progress and timeout warnings; it does not change Azure SDK block sizing.
- `BlobUploadTimeoutSeconds` is the timeout for each upload attempt. Very large files on slow links may need a higher value.

The API HTTP client currently has a fixed five-minute timeout. `HttpTimeoutSeconds` is a read-only code default and is not a bindable setting.

## Upload resilience and partial failures

```json
{
  "BackupClient": {
    "MaxRetryAttempts": 3,
    "RetryDelayMs": 1000,
    "MaxRetryDelayMs": 30000,
    "MaxFailurePercentage": 5,
    "CommitStatusPollIntervalSeconds": 2,
    "CommitStatusTimeoutSeconds": 600
  }
}
```

Blob uploads use exponential backoff with jitter. Retries cover transient Azure failures (`408`, `429`, `500`, `502`, `503`, and `504`) plus network, timeout, and I/O exceptions. `MaxRetryAttempts` is the number of retries after the initial attempt; set it to `0` only when deliberately disabling upload retries.

`MaxFailurePercentage` is an integer percentage. A run aborts when the percentage of failed upload attempts is greater than this value. Individual server-side commit failures may instead produce `CompletedWithErrors`; those files remain pending locally and are retried later.

After submitting a commit, the client polls at `CommitStatusPollIntervalSeconds`. If processing is still queued or active after `CommitStatusTimeoutSeconds`, the client retains its pending-run journal and reconciles the commit on a later run rather than treating the durable server job as failed.

API calls have a separate standard .NET HTTP resilience pipeline under `BackupApiClient:RetryOptions`:

```json
{
  "BackupApiClient": {
    "RetryOptions": {
      "Retry": {
        "MaxRetryAttempts": 3,
        "Delay": "00:00:02",
        "BackoffType": "Exponential"
      }
    }
  }
}
```

## Scaled-to-zero wake-up

Before normal API traffic, the client can probe the hosted Gateway so a scaled-to-zero deployment has time to start:

```json
{
  "BackupApiClient": {
    "WakeUp": {
      "Enabled": true,
      "InitialDelaySeconds": 2,
      "MaxDelaySeconds": 30,
      "MaxWaitSeconds": 180,
      "ProbeTimeoutSeconds": 10,
      "RecheckIntervalSeconds": 60
    }
  }
}
```

The wake-up probe is authenticated for deployed endpoints. In Development, a loopback API URL uses the local anonymous-auth path.

## Locked files

```json
{
  "BackupClient": {
    "LockedFilePolicy": "SkipLocked"
  }
}
```

Available policies:

- `SkipLocked` opens files with read sharing and skips files that a writer has locked.
- `ReadThroughSharedWrites` allows read/write/delete sharing. This can read more open files, but it is not a filesystem snapshot and does not guarantee a consistent copy while a file changes.

## Scheduling

For a long-running service process:

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

`IntervalMinutes` must be greater than zero. Leave `Schedule:Enabled` false for one-shot CLI execution, Windows Task Scheduler, or a systemd timer. See [Client configuration](CLIENT_CONFIGURATION.md#scheduling) for installation examples.

## Client-side encryption

```json
{
  "BackupClient": {
    "Encryption": {
      "Mode": "ClientAndServer",
      "RecoveryPhraseFilePath": null,
      "KdfIterations": 600000
    }
  }
}
```

`ServerSideOnly` uploads plaintext over TLS and relies on Azure Storage encryption at rest. `ClientAndServer` encrypts file content locally before upload. On first use it creates a recovery-phrase file at the configured path or in the client application-data directory.

Keep the recovery phrase outside the backed-up machine. Losing it makes client-encrypted backups unrecoverable; anyone who obtains it can decrypt those backups.

## Restore defaults

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

Run `backup-client restore`. An empty `LogicalPaths` array selects all available files for the chosen device. Archive-tier blobs cannot be restored until they have been rehydrated in Azure; the current product does not automate rehydration.

## Local state database

```json
{
  "Database": {
    "FilePath": null
  }
}
```

With `FilePath` unset, SQLite is stored at:

- Windows: `%LOCALAPPDATA%\backup-client\backup-state.db`
- Linux/macOS: `~/.local/share/backup-client/backup-state.db`

The database contains the device identifier, file fingerprints, pending-run journal, staged-upload records, and scan scratch data. Deleting it forces a new local identity/full comparison and discards resumable state, so stop the client and preserve a copy before troubleshooting this way.

## Telemetry

The client exports through OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, and through Azure Monitor when `OTEL_EXPORTER_AZURE_MONITOR_CONNECTION` is set. There is currently no Zipkin exporter registered.

Activity source and meter: `florisdev.backup.client`

| Instrument | Type | Unit |
| --- | --- | --- |
| `florisdev.backup.files.count` | Counter | files |
| `florisdev.backup.failures` | Counter | failures |
| `florisdev.backup.duration` | Histogram | ms |
| `florisdev.backup.size` | Histogram | bytes |

See [Monitoring](MONITORING.md) for exporter and service health details.

## Complete client example

The maintained example is [`src/services/client/appsettings.example.json`](../src/services/client/appsettings.example.json). It includes the Entra, Gateway, backup, restore, retry, wake-up, database, and telemetry sections without embedding production credentials.

## Related documentation

- [Client configuration](CLIENT_CONFIGURATION.md)
- [.backupignore reference](BACKUPIGNORE.md)
- [Authentication](AUTHENTICATION.md)
- [Monitoring](MONITORING.md)
