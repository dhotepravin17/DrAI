#!/usr/bin/env bash
# Deploys a new version: backup, pull images, migrate, restart the app.
# Run from deploy/vps: bash scripts/update.sh            (uses IMAGE_TAG from .env)
#                      bash scripts/update.sh sha-abc1234 (pins and deploys that tag)
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ -n "${1:-}" ]]; then
  sed -i "s|^IMAGE_TAG=.*|IMAGE_TAG=$1|" .env
fi
set -a; source .env; set +a

echo "== Backup before update =="
bash scripts/backup-now.sh

echo "== Pull ${IMAGE_TAG} =="
docker compose pull api migrate

echo "== Migrate database =="
docker compose run --rm migrate

echo "== Restart app =="
docker compose up -d api caddy

for i in $(seq 1 30); do
  if curl -fsS "https://${DOMAIN}/health" >/dev/null 2>&1; then
    echo "Healthy: https://${DOMAIN}"
    exit 0
  fi
  sleep 2
done
echo "The app did not report healthy within 60 s. Check: docker compose logs --tail 100 api" >&2
exit 1
