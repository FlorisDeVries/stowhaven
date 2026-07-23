#!/usr/bin/env bash
# Installs a published Backup Client build into the current user's XDG data
# directory and launches first-time setup. Run this from inside the folder
# produced by `dotnet publish` (see README.md).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${BACKUP_CLIENT_INSTALL_DIR:-$HOME/.local/share/backup-client}"
BIN_DIR="$HOME/.local/bin"
EXE_NAME="FlorisDeV.BackupClient"
SYSTEMD_USER_DIR="$HOME/.config/systemd/user"
TIMER_UNIT="backup-client"
SCHEDULE_TIME="${BACKUP_CLIENT_SCHEDULE_TIME:-02:00:00}"

if [[ ! -f "$SCRIPT_DIR/$EXE_NAME" ]]; then
    echo "Error: $EXE_NAME not found next to this script. Run install.sh from inside the published output folder." >&2
    exit 1
fi

if [[ "$SCRIPT_DIR" == "$INSTALL_DIR" ]]; then
    echo "Already running from $INSTALL_DIR; nothing to copy."
else
    if [[ -d "$INSTALL_DIR" ]]; then
        echo "Existing install found at $INSTALL_DIR - updating in place (local config is preserved)."
    else
        echo "Installing Backup Client to $INSTALL_DIR..."
    fi

    mkdir -p "$INSTALL_DIR"
    # -a: preserve permissions/timestamps; trailing "/." copies hidden files too (e.g. .backupignore)
    cp -a "$SCRIPT_DIR/." "$INSTALL_DIR/"
fi

chmod +x "$INSTALL_DIR/$EXE_NAME"

mkdir -p "$BIN_DIR"
ln -sf "$INSTALL_DIR/$EXE_NAME" "$BIN_DIR/backup-client"
echo "Linked $BIN_DIR/backup-client -> $INSTALL_DIR/$EXE_NAME"

if [[ ":$PATH:" != *":$BIN_DIR:"* ]]; then
    echo
    echo "Note: $BIN_DIR is not on your PATH."
    echo "Add this to your shell profile (~/.bashrc, ~/.zshrc, etc.) to run 'backup-client' from anywhere:"
    echo "  export PATH=\"\$HOME/.local/bin:\$PATH\""
fi

echo
echo "Launching first-time setup..."
echo

exec "$INSTALL_DIR/$EXE_NAME" configure "$@"
