# Advanced Configuration Guide

Performance tuning, resilience configuration, and advanced scenarios for the Backup Client.

## Table of Contents
- [Performance Tuning](#performance-tuning)
- [Resilience & Error Handling](#resilience--error-handling)
- [Advanced Scenarios](#advanced-scenarios)
- [Diagnostics & Monitoring](#diagnostics--monitoring)

---

## Performance Tuning

### Network-Based Tuning

#### Fast Internet (100+ Mbps)

```json
{
  "BackupClient": {
    "MaxParallelUploads": 8
  }
}
```

**Effect**: More concurrent uploads, faster overall backup
**Trade-off**: Higher CPU/memory usage

---

#### Slow Internet (< 10 Mbps)

```json
{
  "BackupClient": {
    "MaxParallelUploads": 2
  }
}
```

**Effect**: Fewer concurrent uploads, less bandwidth saturation
**Trade-off**: Slower overall backup time

---

### File Type-Based Tuning

#### Many Small Files

```json
{
  "BackupClient": {
    "MaxParallelUploads": 10,
    "LargeFileThresholdBytes": 52428800  // 50MB
  }
}
```

**Use case**: Source code repositories, document collections
**Effect**: High parallelism, less logging for small files
**Optimal for**: < 1MB average file size

---

#### Few Large Files

```json
{
  "BackupClient": {
    "MaxParallelUploads": 2,
    "LargeFileThresholdBytes": 10485760  // 10MB
  }
}
```

**Use case**: Video editing, large datasets, VM images
**Effect**: Lower parallelism, detailed progress for large files
**Optimal for**: > 100MB average file size

---

### Mixed Workloads

```json
{
  "BackupClient": {
    "MaxParallelUploads": 6,
    "LargeFileThresholdBytes": 20971520  // 20MB
  }
}
```

**Use case**: General-purpose backup with mix of file sizes
**Effect**: Balanced approach
**Optimal for**: Varied file sizes

---

## Resilience & Error Handling

The backup client uses **Polly v8** resilience pipelines for automatic retry logic.

### Default Behavior

```json
{
  "BackupClient": {
    "MaxRetryAttempts": 3,
    "RetryDelayMs": 1000,
    "MaxRetryDelayMs": 30000,
    "HttpTimeoutSeconds": 300,
    "BlobUploadTimeoutSeconds": 600,
    "MaxFailurePercentage": 5
  }
}
```

**Retry strategy**:
- Exponential backoff: 1s → 2s → 4s → 8s → (capped at 30s)
- Jitter: Random variation to prevent thundering herd
- Automatic retry for: HTTP 408, 429, 5xx, network timeouts

---

### Unreliable Networks

For spotty WiFi, mobile connections, or high-latency networks:

```json
{
  "BackupClient": {
    "MaxRetryAttempts": 5,
    "RetryDelayMs": 2000,
    "MaxRetryDelayMs": 60000,
    "HttpTimeoutSeconds": 600,
    "BlobUploadTimeoutSeconds": 1800,
    "MaxFailurePercentage": 10
  }
}
```

**Changes**:
- More retry attempts (5 instead of 3)
- Longer initial delay (2s instead of 1s)
- Higher max delay (60s instead of 30s)
- Longer timeouts for slow connections
- More tolerant failure threshold (10% vs 5%)

---

### Large File Uploads

For files > 1GB or slow storage:

```json
{
  "BackupClient": {
    "BlobUploadTimeoutSeconds": 3600,  // 1 hour per attempt
    "MaxRetryAttempts": 5,
    "MaxParallelUploads": 2  // Reduce contention
  }
}
```

---

### Aggressive/Fast-Fail

For reliable networks where you want to fail fast:

```json
{
  "BackupClient": {
    "MaxRetryAttempts": 1,  // Only one retry
    "RetryDelayMs": 500,
    "MaxRetryDelayMs": 5000,
    "HttpTimeoutSeconds": 60,
    "MaxFailurePercentage": 1
  }
}
```

**Use case**: Schedule-based backups with tight time windows

---

### Disable Retries (Not Recommended)

```json
{
  "BackupClient": {
    "MaxRetryAttempts": 0
  }
}
```

**⚠️ Warning**: Only use for testing. Production backups should always have retry logic.

---

## Understanding Failure Handling

### Transient Errors (Auto-Retry)

These errors trigger automatic retries:

- **Network timeouts**: Connection lost, request timeout
- **HTTP 408**: Request Timeout
- **HTTP 429**: Too Many Requests (rate limiting)
- **HTTP 5xx**: Server errors (500, 502, 503, 504)
- **Temporary connection issues**: DNS lookup failures, SSL handshake failures

**Log example**:
```
[Warning] Upload failed for file.txt, attempt 1/3: Request timed out
[Info] Retrying in 1000ms with exponential backoff...
[Info] Upload succeeded for file.txt on attempt 2
```

---

### Permanent Errors (No Retry)

These errors fail immediately without retries:

- **UnauthorizedAccessException**: File permission denied
- **FileNotFoundException**: File deleted during scan
- **HTTP 401**: Authentication failure
- **HTTP 403**: Forbidden (permission issue)
- **HTTP 400**: Bad request (client error)

**Log example**:
```
[Error] Permanent failure for locked-file.txt: Access denied
[Info] Skipping file and continuing with backup...
```

---

### Partial Failures

Backup continues even if individual files fail:

```
[Warning] Batch upload partial failure: 2/100 files failed
[Info] Processed batch: 98 files, 150MB (200 total scanned)
[Warning] Backup completed with partial failures
[Info] Final stats: 198 succeeded, 2 failed (1.0% failure rate)
```

**Failure threshold**: Backup only fails completely if failure rate exceeds `MaxFailurePercentage`.

---

## Advanced Scenarios

### Scenario: Backup Entire Drive (Not Recommended)

```json
{
  "BackupClient": {
    "BackupTargets": {
      "system-drive": "C:\\"
    }
  }
}
```

**Expected behavior**:
- ⚠️ Warning logged about system drive
- Backup proceeds with exclusions applied
- First backup will be very large (plan for several hours)

**Important considerations**:
1. Ensure `.backupignore` excludes system directories (default does this)
2. Add exclusions for large programs/games you can reinstall
3. Consider network bandwidth and Azure storage costs
4. May be better for disaster recovery than incremental backup

---

### Scenario: Multiple Machines to Single Storage

Each machine needs a **unique device ID** (automatically generated on first run).

**Machine 1 - Desktop**: Default configuration
**Machine 2 - Laptop**: Default configuration

Files are automatically organized by device:
```
Azure Storage:
  device-{guid-1}/
    user-profile/Documents/file.txt
  device-{guid-2}/
    user-profile/Documents/file.txt
```

**Device ID location**:
- Windows: `%APPDATA%\backup-client\device-state.db`
- Linux/macOS: `~/.local/share/backup-client/device-state.db`

---

### Scenario: Scheduled Backups

Use OS-native scheduling with optimized settings:

**Windows Task Scheduler**:
```xml
<!-- Run daily at 2 AM with fast-fail -->
<Triggers>
  <CalendarTrigger>
    <StartBoundary>2024-01-01T02:00:00</StartBoundary>
    <ScheduleByDay>
      <DaysInterval>1</DaysInterval>
    </ScheduleByDay>
  </CalendarTrigger>
</Triggers>
```

**Configuration for scheduled backups**:
```json
{
  "BackupClient": {
    "MaxParallelUploads": 8,     // Fast completion
    "MaxRetryAttempts": 5,       // Reliable
    "MaxFailurePercentage": 5    // Fail if too many errors
  }
}
```

---

### Scenario: Backup Over VPN

```json
{
  "BackupClient": {
    "MaxParallelUploads": 2,     // Reduce VPN load
    "MaxRetryAttempts": 5,
    "HttpTimeoutSeconds": 600,    // VPN can be slow
    "BlobUploadTimeoutSeconds": 1800
  }
}
```

---

## Diagnostics & Monitoring

### Logging Configuration

Adjust log levels in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "FlorisDeV.BackupClient": "Debug",  // Detailed backup logs
      "Microsoft": "Warning"
    }
  }
}
```

**Log levels**:
- `Trace`: Everything (very verbose)
- `Debug`: Detailed backup operations
- `Information`: Normal operation (default)
- `Warning`: Issues that don't stop backup
- `Error`: Failures that stop backup
- `Critical`: System-level failures

---

### Telemetry & Metrics

The client exports OpenTelemetry metrics to Application Insights:

**Key metrics**:
- `backup.operation.duration`: Total backup time
- `backup.files.scanned`: Number of files scanned
- `backup.files.uploaded`: Number of files uploaded
- `backup.bytes.transferred`: Total bytes uploaded
- `backup.failures`: Number of failed files

**Activity tracing**: Each backup run creates an Activity with:
- `device.id`: Unique device identifier
- `backup.targets`: Number of targets
- `backup.type`: Full or Incremental

---

### Health Checks

The client provides health check endpoints (when hosted):

- `/health`: Overall health
- `/health/ready`: Ready to accept requests
- `/health/live`: Service is alive

**Health checks include**:
- Azure Blob Storage connectivity
- Dapr state store connectivity
- Database accessibility

---

### State Database

Located at:
- Windows: `%APPDATA%\backup-client\backup-state.db`
- Linux/macOS: `~/.local/share/backup-client/backup-state.db`

**Contains**:
- File metadata (path, hash, size, modified time)
- Last backup timestamp per file
- Device ID
- Last successful backup run

**Maintenance**:
- Automatically managed
- Uses SQLite with write-ahead logging (WAL)
- No manual cleanup needed

**Reset/troubleshooting**:
```bash
# Stop backup client first
rm ~/.local/share/backup-client/backup-state.db
# Next backup will be a full backup
```

---

## Configuration Reference

### All Available Properties

```json
{
  "BackupClient": {
    "BackupTargets": {
      "target-name": "path/to/directory"
    },
    "IgnoreFilePath": "path/to/.backupignore",
    "MaxParallelUploads": 4,
    "LargeFileThresholdBytes": 10485760,
    "MaxRetryAttempts": 3,
    "RetryDelayMs": 1000,
    "MaxRetryDelayMs": 30000,
    "HttpTimeoutSeconds": 300,
    "BlobUploadTimeoutSeconds": 600,
    "MaxFailurePercentage": 5
  },
  "Database": {
    "FilePath": "custom/path/backup-state.db"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

### Property Constraints

| Property | Type | Min | Max | Default |
|----------|------|-----|-----|---------|
| `MaxParallelUploads` | int | 1 | 20 | 4 |
| `LargeFileThresholdBytes` | long | 0 | ∞ | 10485760 (10MB) |
| `MaxRetryAttempts` | int | 0 | 10 | 3 |
| `RetryDelayMs` | int | 100 | 60000 | 1000 |
| `MaxRetryDelayMs` | int | 1000 | 300000 | 30000 |
| `HttpTimeoutSeconds` | int | 10 | 3600 | 300 |
| `BlobUploadTimeoutSeconds` | int | 60 | 7200 | 600 |
| `MaxFailurePercentage` | double | 0 | 100 | 5 |

---

## Related Documentation

- [Quick Start Guide](CLIENT_CONFIGURATION.md#quick-start)
- [.backupignore Reference](BACKUPIGNORE.md)
- [Troubleshooting](CLIENT_CONFIGURATION.md#troubleshooting)
