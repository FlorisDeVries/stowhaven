# .backupignore File Reference

Complete guide to customizing file exclusions for different backup scenarios.

## Table of Contents
- [How It Works](#how-it-works)
- [Default Exclusions](#default-exclusions)
- [Customization by Use Case](#customization-by-use-case)
- [File Location & Priority](#file-location--priority)
- [Best Practices](#best-practices)

---

## How It Works

The `.backupignore` file uses glob patterns (similar to `.gitignore`) to exclude files from backup:

```plaintext
# Comments start with #
**/*.tmp           # Exclude all .tmp files
**/node_modules/** # Exclude node_modules directories
*.log              # Exclude .log files in root only
```

---

## Default Exclusions

The client ships with sensible defaults optimized for **developer workstations**:

- **Temporary files**: `**/*.tmp`, `**/.tmp`
- **Logs**: `**/*.log`, `**/logs/**`
- **Caches**: `**/.cache/**`, `**/Cache/**`
- **Build outputs**: `**/bin/**`, `**/obj/**`, `**/target/**`, `**/dist/**`
- **Dependencies**: `**/node_modules/**`, `**/__pycache__/**`, `**/venv/**`
- **Language-specific caches**: `.pytest_cache`, `.npm`, `.gradle`, `.cargo`
- **Container & IaC**: `**/.docker/**`, `**/.terraform/**`, `terraform.tfstate`
- **Version control**: `**/.git/**`, `**/.svn/**`
- **System files**: `Thumbs.db`, `.DS_Store`
- **Browser caches**: Chrome, Firefox, Edge cache directories
- **Crash dumps**: `**/*.dmp`, `**/*.stackdump`
- **Cloud sync temp files**: `**/*.icloud`, `**/~$*`

📄 [View complete default .backupignore](../src/services/client/.backupignore)

---

## Customization by Use Case

### 🖥️ Developer Workstation (Default)

**No changes needed.** The defaults are perfect for workstations. They exclude:
- Build artifacts you can regenerate
- Dependencies you can reinstall
- Temporary files and caches

While preserving:
- Source code
- Documents
- Configuration files

---

### 🗄️ Linux/Windows Server Backups

**⚠️ Critical adjustments needed:**

#### 1. Keep Log Files

Logs are essential for server auditing and forensics. Comment out:

```plaintext
# **/*.log          # KEEP LOGS FOR SERVER AUDITING
# **/logs/**        # KEEP APPLICATION LOGS
# /var/log/**       # KEEP SYSTEM LOGS (Linux)
```

#### 2. Keep Application Data

Review binary exclusions:

```plaintext
# **/bin/**         # May contain deployed applications
# **/Program Files/**  # Remove if backing up full Windows server
```

#### 3. Keep Monitoring Data

Consider keeping:
- Application logs for forensic analysis
- Audit trails
- Performance metrics
- Security logs

---

### 🏢 Production Systems / Disaster Recovery

Be **conservative** with exclusions. Only exclude files you can **definitely** regenerate:

```plaintext
# KEEP: logs, application binaries, configs, data
# EXCLUDE: Only true temp files and regenerable caches

# Safe to exclude:
**/*.tmp
**/.tmp/**
**/Temp/**
**/cache/**

# Review carefully before excluding:
# **/*.log        # Usually KEEP for production
# **/bin/**       # Usually KEEP for production
# **/node_modules/**  # Can reinstall, but slows recovery
```

**Philosophy:** In DR scenarios, err on the side of backing up too much rather than too little.

---

### 💼 Personal PC / Home Backup

Defaults work well, but consider space optimizations:

```plaintext
# Add at end of .backupignore

# Large media (if backed up elsewhere)
**/Videos/Recordings/**
**/Downloads/*.iso
**/Downloads/*.dmg

# Virtual machines (can be large)
**/VirtualBox VMs/**
**/*.vdi
**/*.vmdk

# Game installations (easily re-downloadable)
**/Steam/steamapps/common/**
**/Epic Games/**
```

---

### 📦 Project-Specific Patterns

#### Docker/DevOps Projects

Already included in defaults:

```plaintext
**/.docker/**
**/.terraform/**
**/.serverless/**
**/.devcontainer/**
terraform.tfstate*
```

#### Data Science Projects

Add to defaults:

```plaintext
# Large datasets (often regenerable or from external sources)
**/datasets/raw/**
**/data/raw/**

# Model files (large, can be retrained)
**/*.h5
**/*.hdf5
**/*.pkl
**/*.pth
**/checkpoints/**
**/models/saved/**

# Jupyter notebook checkpoints
**/.ipynb_checkpoints/**
```

#### Game Development (Unity/Unreal)

Add to defaults:

```plaintext
# Unity
**/Library/**
**/Temp/**
**/Builds/**
**/*.csproj
**/*.unityproj
**/*.sln

# Unreal Engine
**/Binaries/**
**/DerivedDataCache/**
**/Intermediate/**
**/Saved/**
**/*.pdb
```

#### Mobile Development

Add to defaults:

```plaintext
# iOS
**/Pods/**
**/DerivedData/**
**/*.xcworkspace
**/*.xcodeproj

# Android
**/.gradle/**
**/build/**
**/captures/**
**/.externalNativeBuild/**
**/local.properties
```

---

## File Location & Priority

The client uses ignore patterns in this priority order:

### 1. Target-Specific (Highest Priority)

`.backupignore` at the **root** of each backup target:

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

```
C:\Users\YourName\Documents\
  └── .backupignore      ← Used for 'documents' target

D:\Projects\
  └── .backupignore      ← Used for 'projects' target
```

### 2. Global (Fallback)

Custom path via `IgnoreFilePath` configuration:

```json
{
  "BackupClient": {
    "IgnoreFilePath": "C:\\Users\\YourName\\.backupignore-global"
  }
}
```

### Important Limitations

**❌ Nested ignore files NOT supported:**

```
C:\Users\YourName\
  ├── .backupignore         ← ✅ Used (target root)
  └── Projects/
      └── MyProject/
          └── .backupignore ← ❌ NOT recognized (too deep)
```

Unlike Git, the backup client does **not** scan for `.backupignore` files in subdirectories.

---

## Best Practices

### ✅ Do

- **Start with defaults** and add project-specific patterns
- **Comment your exclusions** for future reference:
  ```plaintext
  **/large-datasets/**  # 500GB of training data, stored on NAS
  ```
- **Test incrementally**: Add patterns one at a time and verify
- **Review periodically**: Check what's excluded vs. backed up quarterly
- **Use specific patterns**: `**/build/**` instead of `**/*build*`

### ❌ Don't

- **Blindly use defaults for servers** - Review log and binary exclusions
- **Exclude file types without verification**:
  ```plaintext
  # ❌ Dangerous - might exclude important data
  **/*.dat
  
  # ✅ Better - be specific
  **/cache/*.dat
  ```
- **Forget scope**: `**/*.log` excludes **ALL** `.log` files everywhere
- **Over-exclude in production**: When in doubt, include it

### Pattern Writing Tips

```plaintext
# ❌ Too broad - matches anywhere in path
*build*

# ✅ Specific - matches directory named "build"
**/build/**

# ❌ Inefficient - redundant
**/node_modules/**
**/node_modules/*

# ✅ One pattern sufficient
**/node_modules/**

# ✅ Multiple file extensions
**/*.{tmp,temp,bak}
```

### Testing Your Patterns

1. **Dry run**: Check logs to see what's being scanned
2. **Start small**: Test with a subset of files first
3. **Verify**: Check Azure storage to confirm expected files are backed up
4. **Iterate**: Adjust patterns based on results

---

## Troubleshooting

### Files Not Being Backed Up

**Check if excluded:**

1. Look at your `.backupignore` patterns
2. Remember `**/*.log` excludes ALL `.log` files
3. Check both target-specific and global ignore files

**Common issues:**

```plaintext
# This excludes ALL txt files - probably too broad
**/*.txt

# This only excludes txt in root - more controlled
*.txt

# This excludes txt in specific directory - most controlled
**/logs/*.txt
```

### Pattern Not Working

**Glob pattern gotchas:**

- `*.tmp` - Only matches in current directory
- `**/*.tmp` - Matches in all subdirectories (usually what you want)
- `temp/` - Doesn't work (no wildcards)
- `**/temp/**` - Works (with wildcards)

### Too Many Files Excluded

Review patterns from most to least specific:

```plaintext
# Start with specific
**/node_modules/**

# Then category-specific
**/obj/**
**/bin/**

# Then broad (use cautiously)
**/*.log
```

---

## Reference

### Glob Pattern Syntax

| Pattern | Matches |
|---------|---------|
| `*` | Any characters except path separator |
| `**` | Any characters including path separator |
| `?` | Single character |
| `[abc]` | One character: a, b, or c |
| `{a,b}` | Either a or b |

### Common Patterns

```plaintext
# All files with extension
**/*.tmp

# Specific directory name anywhere
**/node_modules/**

# Files in root only
*.log

# Multiple extensions
**/*.{tmp,bak,old}

# Specific path from root
/var/log/**
```

---

## Related Documentation

- [Quick Start Guide](CLIENT_CONFIGURATION.md#quick-start)
- [Configuration Reference](CLIENT_CONFIGURATION.md#configuration-reference)
- [Troubleshooting](CLIENT_CONFIGURATION.md#troubleshooting)
