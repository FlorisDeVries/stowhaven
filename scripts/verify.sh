#!/usr/bin/env bash
set -euo pipefail

include_bicep=false
for argument in "$@"; do
    case "$argument" in
        --bicep) include_bicep=true ;;
        --help|-h)
            printf '%s\n' 'Usage: bash scripts/verify.sh [--bicep]' \
                'Restore, build, publish the monitoring UI, and run .NET, packaging and workflow tests.' \
                'Add --bicep to compile the infrastructure template. Logs and publish output stay in a temporary directory.'
            exit 0 ;;
        *) printf 'Unknown option: %s\n' "$argument" >&2; exit 2 ;;
    esac
done

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"
for dependency in dotnet python3; do
    command -v "$dependency" >/dev/null || { printf 'Required command is missing: %s\n' "$dependency" >&2; exit 1; }
done
if "$include_bicep"; then
    command -v az >/dev/null || { printf '%s\n' 'Required command is missing: az' >&2; exit 1; }
fi
verification_dir=$(mktemp -d "${TMPDIR:-/tmp}/stowhaven-verify.XXXXXX")
printf 'Verification artifacts: %s\n' "$verification_dir"

run_check() {
    local name=$1
    shift
    printf 'Running %s...\n' "$name"
    if "$@" >"$verification_dir/$name.log" 2>&1; then
        printf 'PASS %s\n' "$name"
    else
        local status=$?
        printf 'FAIL %s (exit %s); full log: %s/%s.log\n' "$name" "$status" "$verification_dir" "$name" >&2
        tail -n 60 "$verification_dir/$name.log" >&2
        return "$status"
    fi
}

run_check restore dotnet restore FlorisDeV.BackupApi.sln --verbosity minimal
run_check build dotnet build FlorisDeV.BackupApi.sln --configuration Release --no-restore --verbosity minimal
run_check publish dotnet publish src/services/monitoring/FlorisDeV.BackupMonitoring.csproj \
    --configuration Release --no-restore --output "$verification_dir/monitoring-ui" --verbosity minimal
run_check dotnet-tests dotnet test FlorisDeV.BackupApi.sln --configuration Release --no-build --verbosity minimal
run_check packaging-tests python3 -m unittest discover -s tests/deployment -p 'test_*.py'
run_check workflow-tests python3 -m unittest discover -s tests/workflow -p 'test_*.py'
if "$include_bicep"; then
    run_check bicep az bicep build --file deploy/bicep/main.bicep --stdout
fi
printf 'All verification checks passed. Full logs: %s\n' "$verification_dir"
