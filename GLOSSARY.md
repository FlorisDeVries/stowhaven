# Stowhaven

Language for backup runs and their completion.

## Language

**Backup attempt**:
One client invocation to perform backup work. An attempt can resume an earlier backup run and create another run after checking the current files.
_Avoid_: Backup run when referring to a client invocation

**Backup run**:
Server-backed backup work that can span several backup attempts, ending successfully only after server completion and local completion.
_Avoid_: Backup attempt when referring to server-backed work

**Local completion**:
The client's recording of a server-finished backup run in its tracked backup state, accounting for accepted files and deletions and ending its pending-run tracking.
_Avoid_: Server commit, upload completion

**Maximum backup age**:
The user-configured age limit after which the latest clean backup outcome is considered overdue.

**Clean backup outcome**:
A successful backup with server completion and local completion, or a completed check finding no changes, without unintended skips, deferred files, or failures. Explicitly excluded files do not make an outcome unclean.

**Clean check**:
A completed examination of the configured backup targets with no changes to back up and no unintended skips, deferred files, or failures. A clean check does not itself create a backup run or establish that a completed server backup exists.
