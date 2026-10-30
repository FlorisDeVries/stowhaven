#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(git -C "$script_dir/.." rev-parse --show-toplevel)
common_dir=$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir)
installed_hooks="$common_dir/stowhaven-hooks"
current_hooks=$(git -C "$repo_root" config --get core.hooksPath || :)

if [ -n "$current_hooks" ] && [ "$current_hooks" != "$installed_hooks" ]; then
    printf '%s\n' 'An existing core.hooksPath is configured. Integrate the versioned guards with those hooks instead of replacing them.' >&2
    exit 1
fi
if [ -z "$current_hooks" ]; then
    for existing_hook in "$common_dir/hooks/"*; do
        case "$existing_hook" in *.sample) continue ;; esac
        if [ -f "$existing_hook" ] && [ -x "$existing_hook" ]; then
            printf '%s\n' 'Active Git hooks already exist. Integrate the versioned guards with them before installing.' >&2
            exit 1
        fi
    done
fi

# Keep installed copies in the common Git directory so guards survive branch
# switches and work in linked worktrees, including branches without .githooks.
mkdir -p "$installed_hooks"
for hook in pre-commit pre-push; do
    cp "$repo_root/.githooks/$hook" "$installed_hooks/$hook"
    chmod +x "$installed_hooks/$hook"
done
git -C "$repo_root" config --local core.hooksPath "$installed_hooks"
printf 'Installed branch guards in %s\n' "$installed_hooks"
