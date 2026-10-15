#!/usr/bin/env bash
# Starts Valmedo inside a GitHub Codespace on port 8080 (web app + API on one address).
#   bash scripts/codespace-start.sh            # private link (only you, signed in to GitHub)
#   bash scripts/codespace-start.sh --public   # anyone with the link can open it
#
# Runs in Development mode: sign-in codes are shown on screen (no SMS needed) and the Super Admin
# (mobile 9000000001) signs in without an authenticator. For testing only, never for real patients.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== Waiting for SQL Server =="
for _ in $(seq 1 60); do
  if (exec 3<>/dev/tcp/127.0.0.1/1433) 2>/dev/null; then break; fi
  sleep 2
done
sleep 5

echo "== Building the web app =="
(cd web && npm run build)
rm -rf src/SmartQueue.Api/wwwroot
cp -r web/dist src/SmartQueue.Api/wwwroot

if [[ "${1:-}" == "--public" && -n "${CODESPACE_NAME:-}" ]]; then
  echo "== Making port 8080 public =="
  gh codespace ports visibility 8080:public -c "$CODESPACE_NAME" || echo "Could not change visibility; use the Ports tab."
fi

if [[ -n "${CODESPACE_NAME:-}" ]]; then
  echo
  echo "Open: https://${CODESPACE_NAME}-8080.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-app.github.dev}"
  echo "Super Admin: 9000000001 (the code is shown on the sign-in screen)"
  echo
fi

echo "== Starting Valmedo on port 8080 (Ctrl+C to stop) =="
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://0.0.0.0:8080
export Bootstrap__ApplyMigrationsOnStartup=true
exec dotnet run --project src/SmartQueue.Api --no-launch-profile
