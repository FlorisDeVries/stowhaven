#!/usr/bin/env bash
# Install a published build. Companion lifecycle operations never configure backups.
set -euo pipefail
umask 077
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
INSTALL_DIR="${BACKUP_CLIENT_INSTALL_DIR:-$HOME/.local/share/backup-client}"
BIN_DIR="${BACKUP_CLIENT_BIN_DIR:-$HOME/.local/bin}"
SYSTEMD_USER_DIR="${BACKUP_CLIENT_SYSTEMD_USER_DIR:-$HOME/.config/systemd/user}"
UNIT_PREFIX="${BACKUP_CLIENT_UNIT_PREFIX:-backup-client}"
DASHBOARD_DIR="${BACKUP_CLIENT_DASHBOARD_INSTALL_DIR:-${INSTALL_DIR}-dashboard}"
EXE_NAME="FlorisDeV.BackupClient"
SCHEDULE_TIME="${BACKUP_CLIENT_SCHEDULE_TIME:-02:00:00}"
SYSTEMD_LAUNCHER="${BACKUP_CLIENT_SYSTEMD_LAUNCHER:-}"
MODE=install
WITH_DASHBOARD=false
case "${1:-}" in
    --with-dashboard) WITH_DASHBOARD=true; shift ;;
    --dashboard-only) MODE=dashboard; WITH_DASHBOARD=true; shift ;;
    --disable-dashboard) MODE=disable; shift ;;
    --remove-dashboard) MODE=remove; shift ;;
    --help|-h)
        echo 'Usage: install.sh [--with-dashboard | --dashboard-only | --disable-dashboard | --remove-dashboard] [configure arguments]'
        echo 'Overrides: BACKUP_CLIENT_INSTALL_DIR, BACKUP_CLIENT_BIN_DIR, BACKUP_CLIENT_SYSTEMD_USER_DIR, BACKUP_CLIENT_UNIT_PREFIX, BACKUP_CLIENT_DASHBOARD_INSTALL_DIR, BACKUP_CLIENT_SCHEDULE_TIME.'
        exit 0 ;;
esac
fail() { echo "Error: $*" >&2; exit 1; }
[[ "$UNIT_PREFIX" =~ ^[a-zA-Z0-9][a-zA-Z0-9_-]{0,100}$ ]] || fail 'Unit prefix must contain only letters, digits, underscores or hyphens (1-101 characters).'
# Reject unsupported unit/shell path syntax before writing anything. Spaces are supported.
for directory in "$INSTALL_DIR" "$BIN_DIR" "$SYSTEMD_USER_DIR" "$DASHBOARD_DIR"; do
    [[ "$directory" == /* && "$directory" != / && "$directory" != *[$'\n\r\t\\"\047%$`']* ]] || fail 'Directories must be absolute and cannot contain control characters, quotes, backslashes, %, $, or backticks.'
    [[ ! -L "$directory" ]] || fail 'Installation directories cannot be symlinks.'
done
for directory_option in INSTALL_DIR BIN_DIR SYSTEMD_USER_DIR DASHBOARD_DIR; do
    canonical_directory="$(realpath -m -- "${!directory_option}")" || fail 'Cannot resolve installation directory.'
    [[ "$canonical_directory" != / ]] || fail 'Installation directories cannot resolve to the filesystem root.'
    printf -v "$directory_option" '%s' "$canonical_directory"
done
LAUNCH_PREFIX=
if [[ -n "$SYSTEMD_LAUNCHER" ]]; then
    [[ "$SYSTEMD_LAUNCHER" == /* && "$SYSTEMD_LAUNCHER" != *[$'\n\r\t\\"\047%$`']* && -x "$SYSTEMD_LAUNCHER" ]] || fail 'Systemd launcher must be an executable absolute path without unsupported characters.'
    LAUNCH_PREFIX="\"$SYSTEMD_LAUNCHER\" "
fi
[[ "$DASHBOARD_DIR" != "$INSTALL_DIR" && "$DASHBOARD_DIR" != "$INSTALL_DIR/"* && "$INSTALL_DIR" != "$DASHBOARD_DIR/"* && ! "$DASHBOARD_DIR" -ef "$INSTALL_DIR" ]] || fail 'Client and companion install directories must be separate, non-nested paths.'
[[ "$SCHEDULE_TIME" =~ ^([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]$ ]] || fail 'Schedule time must be HH:MM:SS.'
if [[ "$WITH_DASHBOARD" == true ]]; then
    command -v python3 >/dev/null || fail 'python3 is required to validate private dashboard configuration.'
    python3 - "$SCRIPT_DIR" "$MODE" "$INSTALL_DIR" <<'PY'
import hashlib, json, pathlib, sys
def validate_payload(root):
    contract = json.loads((root / 'dashboard-contract.json').read_text())
    executable = 'FlorisDeV.BackupClient'
    expected = {executable} if contract['layout'] == 'singleFile' else {executable, executable + '.dll'}
    files = contract['files']
    if (contract['reportingSchemaVersion'] != 1 or contract['backupStateSchemaVersion'] != 3 or
        contract['layout'] not in ('singleFile', 'assemblies') or set(files) != expected or
        any(digest.lower() != hashlib.sha256((root / name).read_bytes()).hexdigest() for name, digest in files.items())):
        raise ValueError('Incompatible or mixed payload')

payloads = [(pathlib.Path(sys.argv[1]), 'Use a complete compatible published dashboard build (reporting schema 1, backup state schema 3).')]
if sys.argv[2] == 'dashboard':
    payloads.append((pathlib.Path(sys.argv[3]), 'Upgrade the installed backup client from a complete compatible published build before --dashboard-only (reporting schema 1, backup state schema 3).'))
for root, guidance in payloads:
    try:
        validate_payload(root)
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        sys.exit(guidance + ' Capability metadata is missing, incompatible or does not match the assembly; no client was executed and no installation files were changed.')
PY
fi
COMPANION_UNIT="$UNIT_PREFIX-dashboard.service"
OPEN_COMMAND="$BIN_DIR/$UNIT_PREFIX-open-dashboard"
# WorkingDirectory is a path setting, not an ExecStart argument list: quotes
# become literal path characters there. Encode spaces using systemd C escapes.
SYSTEMD_WORKING_DIR="${INSTALL_DIR// /\\x20}"
companion_missing_inactive() {
    local unit_state load_state= active_state= key value
    unit_state="$(systemctl --user show --property=LoadState,ActiveState -- "$COMPANION_UNIT")" || fail 'Cannot inspect the companion; check the user session before upgrading/removing.'
    while IFS='=' read -r key value; do
        case "$key" in
            LoadState) [[ -z "$load_state" ]] || fail 'Ambiguous companion load state.'; load_state=$value ;;
            ActiveState) [[ -z "$active_state" ]] || fail 'Ambiguous companion activity state.'; active_state=$value ;;
        esac
    done <<< "$unit_state"
    [[ "$load_state" == not-found && "$active_state" == inactive ]]
}
if [[ "$MODE" == disable || "$MODE" == remove ]]; then
    command -v systemctl >/dev/null || fail 'systemctl is required; run in the installed user session.'
    if ! companion_missing_inactive; then
        systemctl --user disable --now "$COMPANION_UNIT" || fail 'Cannot disable companion. Check the systemd user session; backup units were not changed.'
    fi
    if [[ "$MODE" == remove ]]; then
        rm -f -- "$SYSTEMD_USER_DIR/$COMPANION_UNIT" "$OPEN_COMMAND"
        # Preserve payload/configuration for rollback; never recursively remove user data.
        systemctl --user daemon-reload
        echo "Removed companion service and open command. Payload retained at $DASHBOARD_DIR for rollback; backup state and reporting are preserved."
    fi
    exit 0
fi
[[ -f "$SCRIPT_DIR/$EXE_NAME" ]] || fail "$EXE_NAME not found next to this script. Run from a published output folder."
# Copy binaries/settings while preserving operator-owned local configuration.
copy_payload() {
    local destination=$1 entry
    mkdir -p -- "$destination"
    chmod 0700 "$destination"
    if [[ "$SCRIPT_DIR" != "$destination" ]]; then
        shopt -s dotglob nullglob
        for entry in "$SCRIPT_DIR"/*; do
            [[ "${entry##*/}" == appsettings.local.json ]] && continue
            cp -a -- "$entry" "$destination/"
        done
    fi
    chmod +x "$destination/$EXE_NAME"
}
mkdir -p -- "$BIN_DIR"
if [[ ":$PATH:" != *":$BIN_DIR:"* ]]; then
    echo "Note: $BIN_DIR is not on PATH. Add it to your shell profile, or use the installed commands by their absolute paths."
fi
if [[ "$MODE" == install ]]; then
    copy_payload "$INSTALL_DIR"
    ln -sf -- "$INSTALL_DIR/$EXE_NAME" "$BIN_DIR/$UNIT_PREFIX"
    SYSTEMD_TIMER_CONFIGURED=false
    if command -v systemctl >/dev/null; then
        mkdir -p -- "$SYSTEMD_USER_DIR"
        cat > "$SYSTEMD_USER_DIR/$UNIT_PREFIX.service" <<UNIT
[Unit]
Description=Stowhaven Client
[Service]
Type=oneshot
SuccessExitStatus=2
WorkingDirectory=$SYSTEMD_WORKING_DIR
ExecStart=${LAUNCH_PREFIX}"$INSTALL_DIR/$EXE_NAME"
UMask=0077
UNIT
        cat > "$SYSTEMD_USER_DIR/$UNIT_PREFIX.timer" <<UNIT
[Unit]
Description=Run Stowhaven Client daily
[Timer]
OnCalendar=*-*-* $SCHEDULE_TIME
Unit=$UNIT_PREFIX.service
Persistent=true
[Install]
WantedBy=timers.target
UNIT
        SYSTEMD_TIMER_CONFIGURED=true
    else
        echo 'systemctl not found; configure manual scheduling as described in README.md.'
    fi
    echo 'Launching first-time setup...'
    "$INSTALL_DIR/$EXE_NAME" configure "$@"
    if [[ -f "$INSTALL_DIR/appsettings.local.json" ]]; then chmod 0600 "$INSTALL_DIR/appsettings.local.json"; fi
    if [[ "$SYSTEMD_TIMER_CONFIGURED" == true ]]; then
        if systemctl --user link "$SYSTEMD_USER_DIR/$UNIT_PREFIX.service" && systemctl --user daemon-reload && systemctl --user enable --now "$SYSTEMD_USER_DIR/$UNIT_PREFIX.timer"; then
            echo "Enabled $UNIT_PREFIX.timer at $SCHEDULE_TIME. User timers require a logged-in session or loginctl enable-linger."
        else
            echo "User session unavailable. Unit files are in $SYSTEMD_USER_DIR; reload and enable $UNIT_PREFIX.timer when the session is available."
        fi
    fi
fi
if [[ "$WITH_DASHBOARD" == true ]]; then
    command -v systemctl >/dev/null || fail 'The companion requires Linux systemd user services.'
    config="$INSTALL_DIR/appsettings.local.json"
    [[ -f "$config" && ! -L "$config" && -O "$config" ]] || fail 'Companion requires an existing user-owned appsettings.local.json in the client installation.'
    [[ "$(stat -c %a "$config")" == 600 ]] || fail 'Keep appsettings.local.json private (chmod 600) before installing the companion.'
    python3 - "$config" "$MODE" "$UNIT_PREFIX" <<'PY'
import json, os, sys, tempfile
path, mode, prefix = sys.argv[1:]
with open(path) as stream:
    settings = json.load(stream)
dashboard = settings.setdefault('BackupClient', {}).setdefault('Dashboard', {})
if mode == 'install':
    dashboard.update(Enabled=True, BackupServiceUnit=prefix + '.service', BackupTimerUnit=prefix + '.timer')
    descriptor, temporary = tempfile.mkstemp(dir=os.path.dirname(path), prefix='.dashboard-config-')
    try:
        with os.fdopen(descriptor, 'w') as stream:
            json.dump(settings, stream, indent=2)
            stream.write('\n')
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary): os.unlink(temporary)
elif dashboard.get('Enabled') is not True:
    sys.exit('Enable BackupClient:Dashboard:Enabled=true in the existing private client configuration before --dashboard-only. No backup settings were changed.')
PY
    # Stop only the previous companion before replacing its payload. Active backups continue.
    if [[ -f "$SYSTEMD_USER_DIR/$COMPANION_UNIT" ]]; then
        if ! companion_missing_inactive; then
            systemctl --user stop "$COMPANION_UNIT" || fail 'Cannot stop existing companion; check the user session before upgrading.'
        fi
    fi
    copy_payload "$DASHBOARD_DIR"
    ln -sf -- "$config" "$DASHBOARD_DIR/appsettings.local.json"
    mkdir -p -- "$SYSTEMD_USER_DIR"
    cat > "$SYSTEMD_USER_DIR/$COMPANION_UNIT" <<UNIT
[Unit]
Description=Stowhaven local dashboard companion
[Service]
Type=simple
WorkingDirectory=$SYSTEMD_WORKING_DIR
ExecStart=${LAUNCH_PREFIX}"$DASHBOARD_DIR/$EXE_NAME" dashboard --BackupClient:Dashboard:BackupServiceUnit=$UNIT_PREFIX.service --BackupClient:Dashboard:BackupTimerUnit=$UNIT_PREFIX.timer
Restart=on-failure
RestartSec=3
UMask=0077
[Install]
WantedBy=default.target
UNIT
    # %q produces a Bash-quoted literal; no path is interpreted as shell code.
    { echo '#!/usr/bin/env bash'; printf 'exec %q open-dashboard %q %q "$@"\n' "$DASHBOARD_DIR/$EXE_NAME" "--BackupClient:Dashboard:BackupServiceUnit=$UNIT_PREFIX.service" "--BackupClient:Dashboard:BackupTimerUnit=$UNIT_PREFIX.timer"; } > "$OPEN_COMMAND"
    chmod 0700 "$OPEN_COMMAND"
    systemctl --user daemon-reload
    systemctl --user enable --now "$SYSTEMD_USER_DIR/$COMPANION_UNIT" || fail "Companion enablement failed. Check the user session, then enable $COMPANION_UNIT."
    echo "Companion enabled. Run $OPEN_COMMAND to authorize a local browser; enable Dashboard.Enabled in the shared private configuration to record backups."
fi
