#!/usr/bin/env bash
# Restores a backup over the live database. Stops the app during the restore.
# Run from deploy/vps: bash scripts/restore.sh backups/SmartQueue_20261015_2100.bak.gz
set -euo pipefail
cd "$(dirname "$0")/.."

file="${1:?Usage: restore.sh backups/SmartQueue_<stamp>.bak.gz}"
[[ -f "$file" ]] || { echo "Not found: $file" >&2; exit 1; }
set -a; source .env; set +a

read -r -p "This replaces the live database with $(basename "$file"). Type RESTORE to continue: " answer
[[ "$answer" == "RESTORE" ]] || { echo "Cancelled."; exit 1; }

bak="${file%.gz}"
if [[ "$file" == *.gz ]]; then gunzip -kf "$file"; fi
name="$(basename "$bak")"

docker compose stop api
docker compose exec db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" -b -Q \
  "ALTER DATABASE [SmartQueue] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   RESTORE DATABASE [SmartQueue] FROM DISK = N'/var/opt/mssql/backups/${name}' WITH REPLACE, CHECKSUM, RECOVERY;
   ALTER DATABASE [SmartQueue] SET MULTI_USER;"
[[ "$file" == *.gz ]] && rm -f "$bak"

docker compose run --rm migrate      # applies any migrations newer than the backup
docker compose start api
echo "Restored $(basename "$file"). Check https://${DOMAIN}/health"
