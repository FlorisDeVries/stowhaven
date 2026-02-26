# Backup Client Configuration Guide

## Recommended Setup

### 1. **What to Backup**

The backup client is designed to backup **your personal data**, not system files or programs.

#### ✅ **Recommended Directories:**

**Windows:**
```json
{
  "BackupClient": {
    "BackupTargets": {
      "user-profile": "C:\\Users\\YourUsername"
    }
  }
}
```

**Linux:**
```json
{
  "BackupClient": {
    "BackupTargets": {
      "user-profile": "/home/yourusername"
    }
  }
}
```

**macOS:**
```json
{
  "BackupClient": {
    "BackupTargets": {
      "user-profile": "/Users/yourusername"
    }
  }
}
```

#### ⚠️ **Not Recommended:**
- Entire system drives (`C:\`, `/`)
- System directories (`C:\Windows`, `/usr`, `/bin`)
- Program installation directories

**Why?** These contain:
- 100-500GB of OS files that are better reinstalled
- Locked files that can't be read during backup
- Files that change constantly (temp, logs, cache)
- Non-portable data that won't work on different machines

---

## 2. **Exclusion Patterns (.backupignore)**

The `.backupignore` file uses glob patterns to exclude files:

### Default Exclusions (Already Included)

The client ships with sensible defaults that exclude:
- Temporary files (`**/*.tmp`, `**/.tmp`)
- Logs (`**/*.log`)
- Caches (`**/.cache/**`, `**/Cache/**`)
- Build outputs (`**/bin/**`, `**/obj/**`, `**/target/**`)
- Dependencies (`**/node_modules/**`, `**/__pycache__/**`)
- Version control (`.git`, `.svn`)
- System files (`Thumbs.db`, `.DS_Store`)

### Custom Exclusions

Add project-specific exclusions:

```plaintext
# My custom exclusions
**/my-large-dataset/**
**/videos/raw-footage/**
*.iso
*.dmg
```

---

## 3. **Configuration Options**

### Full Configuration Example

```json
{
  "BackupClient": {
    "BackupTargets": {
      "user-profile": "C:\\Users\\YourName",
      "projects": "D:\\Projects",
      "photos": "E:\\Photos"
    },
    "IgnoreFilePath": null,  // Uses .backupignore in target directories
    "MaxParallelUploads": 4,  // Concurrent file uploads (adjust for bandwidth)
    "LargeFileThresholdBytes": 10485760,  // 10MB - progress tracking threshold
    "ExcludePatterns": [
      "**/*.iso",
      "**/my-temp-project/**"
    ]
  },
  "Database": {
    "FilePath": null  // Defaults to %APPDATA% or ~/.local/share/backup-client
  }
}
```

### Configuration Properties

| Property | Default | Description |
|----------|---------|-------------|
| `BackupTargets` | **(Required)** | Dictionary of named directories to backup. Keys are target names (used as storage prefixes), values are directory paths |
| `IgnoreFilePath` | `null` | Path to custom .backupignore file. If null, looks for `.backupignore` in target directory |
| `MaxParallelUploads` | `4` | Number of concurrent file uploads. Higher = faster but more bandwidth |
| `LargeFileThresholdBytes` | `10485760` (10MB) | Files larger than this get progress tracking |
| `ExcludePatterns` | `[]` | Additional exclusions (combined with .backupignore) |
| `MaxRetryAttempts` | `3` | Maximum retry attempts for transient failures (network errors, timeouts). Set to 0 to disable |
| `RetryDelayMs` | `1000` | Initial retry delay in milliseconds. Uses exponential backoff (doubles each retry) |
| `MaxRetryDelayMs` | `30000` | Maximum delay between retries in milliseconds (30 seconds) |
| `HttpTimeoutSeconds` | `300` | HTTP request timeout for API calls (5 minutes) |
| `BlobUploadTimeoutSeconds` | `600` | Blob upload timeout per attempt (10 minutes). Large files may need longer |
| `MaxFailurePercentage` | `5` | Maximum percentage of files allowed to fail (5%). Backup fails if exceeded |

---

## 4. **Common Scenarios**

### Scenario A: Backup Home Directory (Recommended)

**Windows:**
```json
{
  "BackupClient": {
    "BackupTargets": {
      "user-profile": "C:\\Users\\John"
    }
  }
}
```

**Result:** Backs up Documents, Downloads, Pictures, Desktop, etc. with smart exclusions.

---

### Scenario B: Backup Specific Project Folder

```json
{
  "BackupClient": {
    "BackupTargets": {
      "projects": "D:\\MyProjects"
    }
  }
}
```

Add to `.backupignore`:
```plaintext
**/node_modules/**
**/venv/**
**/bin/**
**/obj/**
```

---

### Scenario C: Backup Multiple Important Folders

Backup multiple directories with a single configuration:

```json
{
  "BackupClient": {
    "BackupTargets": {
      "documents": "D:\\ImportantData\\Documents",
      "projects": "D:\\ImportantData\\Projects",
      "photos": "E:\\Photos"
    }
  }
}
```

Each target gets its own storage prefix:
```
Storage:
  documents/file.txt
  projects/README.md
  photos/vacation.jpg
  ├── Documents\
  ├── Projects\
  └── Photos\
```

---

### Scenario D: Advanced - Backup Entire Drive with Exclusions

⚠️ **Warning:** This will generate warnings but is allowed.

```json
{
  "BackupClient": {
    "BackupTargets": {
      "system-drive": "C:\\"
    }
  }
}
```

**Ensure comprehensive exclusions** in `.backupignore`:
- System directories are already excluded by default
- Add custom exclusions for large media libraries, games, etc.

**Expected behavior:**
- Warning logged: _"Backing up entire system drive is not recommended..."_
- Backup proceeds with exclusions applied
- First backup will be large and slow

---

## 5. **Performance Tuning**

### Fast Internet (100+ Mbps)
```json
{
  "BackupClient": {
    "MaxParallelUploads": 8
  }
}
```

### Slow Internet (< 10 Mbps)
```json
{
  "BackupClient": {
    "MaxParallelUploads": 2
  }
}
```

### Many Small Files
```json
{
  "BackupClient": {
    "MaxParallelUploads": 10,
    "LargeFileThresholdBytes": 52428800  // 50MB - less logging for small files
  }
}
```

### Few Large Files (Video editing, etc.)
```json
{
  "BackupClient": {
    "MaxParallelUploads": 2,
    "LargeFileThresholdBytes": 10485760  // 10MB - track progress
  }
}
```

---

## 6. **Resilience & Error Handling**

The backup client uses **Polly** (via Microsoft.Extensions.Http.Resilience) for automatic retry logic with exponential backoff.

### Default Retry Behavior

- **Framework**: Polly v8 resilience pipelines for battle-tested retry logic
- **Automatic Retries:** 3 attempts for network errors, timeouts, and throttling (HTTP 429, 5xx)
- **Exponential Backoff:** 1s, 2s, 4s, 8s... up to 30s between retries
- **Jitter**: Random delay variation to prevent thundering herd problems
- **Partial Failures:** Backup continues if individual files fail
- **Failure Threshold:** Backup fails if >5% of files fail to upload

### Retry Configuration for Unreliable Networks

```json
{
  "BackupClient": {
    "MaxRetryAttempts": 5,
    "RetryDelayMs": 2000,
    "MaxRetryDelayMs": 60000,
    "MaxFailurePercentage": 10
  }
}
```

### Timeout Configuration for Large Files or Slow Connections

```json
{
  "BackupClient": {
    "HttpTimeoutSeconds": 600,
    "BlobUploadTimeoutSeconds": 1800,
    "MaxRetryAttempts": 5
  }
}
```

### Understanding Failure Handling

**Transient Errors (Auto-Retry):**
- Network timeouts
- HTTP 408 (Request Timeout), 429 (Too Many Requests), 5xx (Server Errors)
- Temporary connection issues

**Permanent Errors (No Retry):**
- File access denied (UnauthorizedAccessException)
- File not found (deleted during scan)
- Invalid authentication

**Partial Failure Example:**
```
[Warning] Batch upload partial failure: 2/100 files failed to upload
[Info] Processed batch: 98 files, 150MB (200 total scanned)
[Warning] Backup completed with partial failures: 198 succeeded, 2 failed (1.0%)
```

### Disable Retries (Not Recommended)

```json
{
  "BackupClient": {
    "MaxRetryAttempts": 0
  }
}
```

---

## 7. **Validation Warnings**

The client performs validation before backup:

### Error: Directory Doesn't Exist
```
Backup target directory does not exist: D:\NonExistent
```
**Fix:** Create the directory or correct the path.

### Error: Insufficient Permissions
```
Insufficient permissions to read backup target directory: C:\Windows\System32
```
**Fix:** Choose a directory you have read access to, or run with appropriate permissions.

### Warning: Backing Up System Drive
```
Backing up entire system drive (C:\) is not recommended. 
Consider using: C:\Users\YourName or add comprehensive exclusions.
```
**Action:** This is a warning, not an error. Backup will proceed. Consider:
1. Using recommended user directory instead
2. Ensuring `.backupignore` has comprehensive exclusions
3. Accepting larger backup size and slower initial sync

---

## 8. **Best Practices**

✅ **DO:**
- Backup your user profile directory (`C:\Users\YourName`, `/home/user`)
- Use `.backupignore` to exclude temp files, caches, and build outputs
- Test restore process periodically
- Monitor first backup to ensure exclusions work correctly

❌ **DON'T:**
- Backup entire system drives without careful exclusion planning
- Backup program installation directories
- Include large media that's easily re-downloadable (Steam games, etc.)
- Backup without testing that you can restore

---

## 9. **Getting Started**

1. **Edit `appsettings.json`:**
   ```json
   {
     "BackupClient": {
       "BackupTargets": {
         "user-profile": "C:\\Users\\YourName"
       }
     }
   }
   ```

2. **Review `.backupignore`:**
   - Default exclusions are comprehensive
   - Add project-specific patterns as needed

3. **Run first backup:**
   ```bash
   dotnet run
   ```

4. **Monitor logs:**
   - Check for warnings about system directories
   - Verify files are being scanned correctly
   - Confirm exclusions are working

5. **Check backup in Azure:**
   - Verify correct files were uploaded
   - Check size is reasonable

---

## 10. **Troubleshooting**

### "Backup validation warning" in logs
**Cause:** Backing up system or root directories.  
**Solution:** Either accept the warning or change to recommended directory.

### Backup is very slow
**Cause:** Too many small files or low bandwidth.  
**Solution:** 
- Reduce `MaxParallelUploads`
- Add more exclusions to `.backupignore`
- Check network speed

### Some files not backing up
**Cause:** Excluded by `.backupignore` or permission denied.  
**Solution:**
- Review `.backupignore` patterns
- Check file permissions
- Look for "access denied" in logs

### Database lock errors
**Cause:** Multiple backup instances running simultaneously.  
**Solution:** Ensure only one instance runs at a time.

---

## Questions?

See the main README or check logs for detailed error messages.
