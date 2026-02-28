# Backup Client Configuration Guide

Quick start guide and essential configuration for the Azure Backup Client.

---

## 📚 Documentation Index

- **This guide**: Quick start + essential configuration
- **[.backupignore Reference](BACKUPIGNORE.md)**: File exclusion patterns and customization
- **[Advanced Configuration](ADVANCED_CONFIGURATION.md)**: Performance tuning, resilience, and complex scenarios
- **[Testing Guide](TESTING.md)**: How to test the backup client
- **[Monitoring Guide](MONITORING.md)**: Observability and diagnostics

---

## Quick Start

### 1. Edit Configuration

Edit `appsettings.json` in your backup client directory:

**Windows:**
```json
{
  "BackupClient": {
    "BackupTargets": {
      "my-files": "C:\\Users\\YourName"
    }
  }
}
```

**Linux/macOS:**
```json
{
  "BackupClient": {
    "BackupTargets": {
      "my-files": "/home/yourname"
    }
  }
}
```

### 2. Run First Backup

```bash
cd src/services/client
dotnet run
```

### 3. Verify

Check the logs for:
- ✅ "Scanning directories: 1 targets"
- ✅ "Backup completed successfully"
- ⚠️ Any warnings about excluded files or system directories

### 4. Check Azure Storage

Verify files were uploaded:
```
Azure Blob Storage > backups container > device-{guid}/ > my-files/
```

**That's it!** The backup client uses smart defaults that work for most users.

---

## What Gets Backed Up?

### ✅ Included (Default)

- Documents, Pictures, Downloads, Desktop
- Source code and projects
- Configuration files
- Personal data

### ❌ Excluded (Default)

The client automatically excludes:
- **Build outputs**: `bin/`, `obj/`, `target/`, `dist/`
- **Dependencies**: `node_modules/`, `venv/`, `__pycache__/`
- **Caches**: `.cache/`, `.npm/`, `.gradle/`
- **Logs**: `*.log`, `logs/`
- **Temp files**: `*.tmp`, `.tmp/`
- **Version control**: `.git/`, `.svn/`
- **System files**: `Thumbs.db`, `.DS_Store`

📄 [See full list of exclusions](BACKUPIGNORE.md#default-exclusions)

---

## Common Configurations

### Backup Multiple Folders

```json
{
  "BackupClient": {
    "BackupTargets": {
      "documents": "C:\\Users\\YourName\\Documents",
      "projects": "D:\\Projects",
      "photos": "E:\\Photos"
    }
  }
}
```

Each target is backed up separately and can have its own `.backupignore` file.

---

### Customize File Exclusions

Create `.backupignore` in your backup target directory:

```plaintext
# Add your custom exclusions
**/my-large-dataset/**
**/videos/raw-footage/**
*.iso
*.vmdk
```

📖 [Complete .backupignore guide](BACKUPIGNORE.md)

---

### Adjust Upload Speed

**Fast internet (100+ Mbps):**
```json
{
  "BackupClient": {
    "MaxParallelUploads": 8
  }
}
```

**Slow internet (< 10 Mbps):**
```json
{
  "BackupClient": {
    "MaxParallelUploads": 2
  }
}
```

🔧 [Performance tuning guide](ADVANCED_CONFIGURATION.md#performance-tuning)

---

## Configuration Reference

### Essential Properties

| Property | Default | Description |
|----------|---------|-------------|
| `BackupTargets` | **(Required)** | Directories to backup. Key = name, Value = path |
| `MaxParallelUploads` | `4` | Number of concurrent file uploads (1-20) |
| `IgnoreFilePath` | `null` | Path to global `.backupignore` file |

### Complete Example

```json
{
  "BackupClient": {
    "BackupTargets": {
      "user-profile": "C:\\Users\\YourName",
      "projects": "D:\\Projects"
    },
    "MaxParallelUploads": 4,
    "IgnoreFilePath": null
  }
}
```

📋 [All configuration options](ADVANCED_CONFIGURATION.md#configuration-reference)

---

## Recommendations

### ✅ DO Backup

**User directories:**
- Windows: `C:\Users\YourName`
- Linux: `/home/yourusername`
- macOS: `/Users/yourusername`

**Why?** Contains your documents, projects, and personal files.

### ⚠️ DON'T Backup

**System directories:**
- `C:\Windows`, `/usr`, `/bin`
- `C:\Program Files`
- Entire drives (`C:\`, `/`)

**Why?** 
- 100-500GB of OS files better reinstalled
- Locked files that can't be read
- Non-portable data

---

## Common Scenarios

### Home Directory Backup

Most common and recommended:

```json
{
  "BackupClient": {
    "BackupTargets": {
      "home": "C:\\Users\\YourName"
    }
  }
}
```

**Result**: Backs up your entire user profile with smart exclusions.

---

### Project Folder Backup

For developers backing up specific projects:

```json
{
  "BackupClient": {
    "BackupTargets": {
      "projects": "D:\\MyProjects"
    }
  }
}
```

Add `D:\MyProjects\.backupignore`:
```plaintext
**/node_modules/**
**/venv/**
**/bin/**
**/obj/**
**/target/**
```

---

### Server Backup

**⚠️ Important**: Server backups need different exclusions!

```json
{
  "BackupClient": {
    "BackupTargets": {
      "app-data": "/var/www/myapp"
    }
  }
}
```

Create `/var/www/myapp/.backupignore`:
```plaintext
# Keep logs for servers!
# **/*.log        # Comment this out

# Keep application binaries
# **/bin/**       # Comment this out

# Still exclude temp/cache
**/*.tmp
**/cache/**
```

📖 [Server backup guide](BACKUPIGNORE.md#linux-windows-server-backups)

---

## Troubleshooting

### Backup is slow

**Check**:
1. Network speed: `MaxParallelUploads` too high?
2. Too many files: Review `.backupignore` patterns
3. File size: Large files take longer

**Solution**:
```json
{
  "BackupClient": {
    "MaxParallelUploads": 2  // Reduce for slow networks
  }
}
```

---

### Files not being backed up

**Causes**:
1. Excluded by `.backupignore`
2. Permission denied
3. File locked by another program

**Check**:
```bash
# Look for exclusion patterns in default ignore file
cat src/services/client/.backupignore

# Look for access denied errors in logs
grep "access denied" backup.log
```

---

### "Backup validation warning" in logs

```
[Warning] Backing up entire system drive (C:\) is not recommended
```

**This is a warning, not an error.** Backup will still proceed.

**Options**:
1. Change target to user directory: `C:\Users\YourName`
2. Add comprehensive exclusions to `.backupignore`
3. Accept warning if you understand implications

---

### Database lock errors

```
[Error] Database is locked
```

**Cause**: Multiple backup instances running simultaneously

**Solution**: Ensure only one backup client runs at a time

---

## Validation Checks

The client validates configuration before starting:

### ❌ Error: Directory doesn't exist

```
Backup target directory does not exist: D:\NonExistent
```

**Fix**: Create directory or correct path

### ❌ Error: Insufficient permissions

```
Insufficient permissions to read: C:\Windows\System32
```

**Fix**: Choose directory you have read access to

### ⚠️ Warning: System drive

```
Backing up entire system drive is not recommended
```

**Fix**: Change to user directory or add exclusions

---

## Best Practices

### ✅ Do

- **Backup user directories** first
- **Test restore** periodically
- **Review exclusions** after first backup
- **Monitor first backup** carefully
- **Use `.backupignore`** for project-specific exclusions

### ❌ Don't

- Backup system drives without careful planning
- Include easily re-downloadable files (Steam games, etc.)
- Backup without testing restore
- Run multiple instances simultaneously

---

## Next Steps

### For Most Users

The defaults work great! Just:
1. Configure `BackupTargets` with your user directory
2. Run the backup
3. Verify files in Azure Storage

### For Advanced Users

- **Customize exclusions**: [.backupignore guide](BACKUPIGNORE.md)
- **Tune performance**: [Performance guide](ADVANCED_CONFIGURATION.md#performance-tuning)
- **Configure resilience**: [Error handling guide](ADVANCED_CONFIGURATION.md#resilience--error-handling)
- **Set up monitoring**: [Monitoring guide](MONITORING.md)

---

## Getting Help

**Documentation**:
- [.backupignore Reference](BACKUPIGNORE.md)
- [Advanced Configuration](ADVANCED_CONFIGURATION.md)
- [Technical Design](TECHNICAL_DESIGN.md)

**Logs**: Check the console output and log files for detailed error messages

**Issues**: Look for patterns in the logs:
- `[Error]` - Failures that stopped backup
- `[Warning]` - Issues that didn't stop backup
- `[Info]` - Normal operation details

**Common patterns**:
```bash
# Find errors
grep "\[Error\]" backup.log

# Find excluded files
grep "excluded" backup.log

# Check upload stats
grep "Backup completed" backup.log
```
