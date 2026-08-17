# `.backupignore` reference

The Stowhaven client uses exclusion globs from `Microsoft.Extensions.FileSystemGlobbing`. Patterns are evaluated relative to each configured backup target.

## Basic format

Use one exclusion pattern per line. Empty lines and lines whose first non-whitespace character is `#` are ignored.

```text
# Temporary files anywhere below the target
*.tmp

# A directory named node_modules anywhere below the target
**/node_modules/**

# One path relative to the target root
private/export.csv
```

Comments must be on their own line. An inline suffix such as `*.tmp # temporary` is treated as part of the pattern and will normally not match.

The format is similar to `.gitignore`, but it is not a complete `.gitignore` implementation. In particular, negation rules such as `!keep.txt` are not supported by the parser.

## Pattern behavior

| Pattern | Meaning in this client |
| --- | --- |
| `*` | Zero or more characters within one path segment |
| `?` | One character within a path segment |
| `**` | Matches across directory levels |
| `*.log` | Special client normalization: excludes `.log` files recursively |
| `**/logs/**` | Excludes directories named `logs` and their contents at any depth |
| `build/output.zip` | Excludes that path relative to the target root |

A simple extension pattern beginning with `*.` and containing no slash, such as `*.tmp`, is normalized by the client to `**/*.tmp`. Use separate lines for multiple extensions:

```text
*.tmp
*.bak
*.old
```

Use `/` in portable patterns. Avoid relying on brace expansion, character classes, leading-root slash semantics, or other Git-specific syntax; these are not part of the behavior tested by this repository.

## File selection and priority

For each target, the client chooses exactly one pattern source:

1. If `<target>/.backupignore` exists, it is used for that target.
2. Otherwise, `BackupClient:IgnoreFilePath` is used as the fallback.
3. If neither file exists, the target is scanned without exclusion patterns.

Target and fallback patterns are not merged. Nested `.backupignore` files below the target root are not discovered.

A relative `IgnoreFilePath` is resolved from the executable directory, not the shell's current working directory. This keeps scheduled tasks and services consistent.

Example configuration:

```json
{
  "BackupClient": {
    "BackupTargets": {
      "documents": "C:\\Users\\Alice\\Documents",
      "projects": "D:\\Projects"
    },
    "IgnoreFilePath": ".backupignore"
  }
}
```

If `D:\Projects\.backupignore` exists, it replaces the configured fallback for `projects`. The fallback still applies to `documents` if that target has no local ignore file.

The `.backupignore` file itself is not implicitly excluded. Add `.backupignore` as a pattern if it should not be backed up.

## Shipped defaults

The client publishes the repository's [default `.backupignore`](../src/services/client/.backupignore) beside the executable. It is aimed at a developer workstation and excludes many logs, caches, dependency folders, build outputs, browser data, and system paths.

Review it before using the client for a server, whole drive, or disaster-recovery workload. Regenerable developer artifacts and operationally important server files have very different retention needs.

Common additions for a developer target:

```text
**/node_modules/**
**/bin/**
**/obj/**
**/.git/**
*.tmp
```

A conservative server-specific file might instead contain only known disposable locations:

```text
# Application-owned temporary data
app/cache/**
app/tmp/**
```

## Safe verification workflow

The client has no dedicated dry-run command. To validate a new pattern set:

1. Copy a small representative directory to a temporary location.
2. Configure that copy as a temporary backup target with its own `.backupignore`.
3. Run a backup and review the client logs.
4. Use restore listing or a restore into a separate directory to confirm the intended files are present.

Be especially careful with broad rules such as `*.log`, `**/build/**`, and `**/bin/**`: they apply at every matching depth.

## Troubleshooting

If a pattern appears ineffective:

- confirm the file is in the selected target and the pattern is relative to that target;
- check whether a target-root `.backupignore` is replacing the configured fallback;
- remove inline comments;
- use `**/directory/**` to exclude a directory at any depth;
- use one extension per line instead of brace syntax;
- remember that already committed files remain in historical backup runs—an ignore rule affects later scans, not existing backup data.

## Related documentation

- [Client configuration](CLIENT_CONFIGURATION.md)
- [Advanced client configuration](ADVANCED_CONFIGURATION.md)
