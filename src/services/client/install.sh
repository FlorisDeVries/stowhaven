#!/usr/bin/env bash
# Installs a published Stowhaven Client build into the current user's XDG data
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
        echo "Installing Stowhaven Client to $INSTALL_DIR..."
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

SYSTEMD_TIMER_CONFIGURED=false

if command -v systemctl >/dev/null 2>&1; then
    echo
    echo "Preparing daily systemd timer ($SCHEDULE_TIME)..."
    mkdir -p "$SYSTEMD_USER_DIR"

    SERVICE_UNIT_TMP="$(mktemp "$SYSTEMD_USER_DIR/.${TIMER_UNIT}.service.XXXXXX")"
    TIMER_UNIT_TMP="$(mktemp "$SYSTEMD_USER_DIR/.${TIMER_UNIT}.timer.XXXXXX")"

    cat > "$SERVICE_UNIT_TMP" <<EOF
[Unit]
Description=Stowhaven Client

[Service]
Type=oneshot
ExecStart=$INSTALL_DIR/$EXE_NAME
EOF

    cat > "$TIMER_UNIT_TMP" <<EOF
[Unit]
Description=Run Stowhaven Client daily

[Timer]
OnCalendar=*-*-* $SCHEDULE_TIME
Persistent=true

[Install]
WantedBy=timers.target
EOF

    chmod 0644 "$SERVICE_UNIT_TMP" "$TIMER_UNIT_TMP"
    mv -f "$SERVICE_UNIT_TMP" "$SYSTEMD_USER_DIR/$TIMER_UNIT.service"
    mv -f "$TIMER_UNIT_TMP" "$SYSTEMD_USER_DIR/$TIMER_UNIT.timer"

    SYSTEMD_TIMER_CONFIGURED=true
else
    echo
    echo "Note: systemctl not found; skipping daily timer setup. See README.md for manual scheduling options."
fi

echo
echo "Launching first-time setup..."
echo

"$INSTALL_DIR/$EXE_NAME" configure "$@"

if [[ "$SYSTEMD_TIMER_CONFIGURED" == true ]]; then
    echo
    echo "Enabling daily systemd timer..."

    if systemctl --user daemon-reload 2>/dev/null && systemctl --user enable --now "$TIMER_UNIT.timer" 2>/dev/null; then
        echo "Enabled $TIMER_UNIT.timer - runs daily at $SCHEDULE_TIME."

        if command -v loginctl >/dev/null 2>&1 && [[ "$(loginctl show-user "$(id -un)" --property=Linger --value 2>/dev/null)" != "yes" ]]; then
            echo
            echo "Note: linger is not enabled, so the timer only fires while you're logged in."
            echo "To let it run even when logged out, run:"
            echo "  loginctl enable-linger $(id -un)"
        fi
    else
        echo "Could not talk to a systemd user session (common under WSL/containers)."
        echo "Unit files were written to $SYSTEMD_USER_DIR - enable them manually once systemd --user is available:"
        echo "  systemctl --user daemon-reload && systemctl --user enable --now $TIMER_UNIT.timer"
    fi
fi
