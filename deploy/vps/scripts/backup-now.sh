#!/usr/bin/env bash
# Takes a backup immediately (same format as the nightly one). Run from deploy/vps: bash scripts/backup-now.sh
set -euo pipefail
cd "$(dirname "$0")/.."
docker compose exec backup /bin/bash /scripts/backup-loop.sh --now
ls -lh backups | tail -n 5
