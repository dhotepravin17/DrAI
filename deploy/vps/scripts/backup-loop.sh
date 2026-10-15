#!/usr/bin/env bash
# Runs inside the "backup" container: one full backup a day at BACKUP_HOUR_UTC, gzipped, kept BACKUP_KEEP_DAYS days.
# SQL Server Express cannot compress backups itself, so the .bak file is gzipped afterwards.
set -euo pipefail

SQLCMD=/opt/mssql-tools18/bin/sqlcmd
DIR=/var/opt/mssql/backups
KEEP_DAYS="${BACKUP_KEEP_DAYS:-14}"
HOUR="${BACKUP_HOUR_UTC:-21}"

backup() {
  local stamp file
  stamp="$(date -u +%Y%m%d_%H%M)"
  file="SmartQueue_${stamp}.bak"
  echo "$(date -u) starting backup ${file}"
  "$SQLCMD" -C -S db -U sa -P "$SA_PASSWORD" -b -Q \
    "BACKUP DATABASE [SmartQueue] TO DISK = N'${DIR}/${file}' WITH INIT, CHECKSUM, NAME = N'Valmedo nightly'"
  "$SQLCMD" -C -S db -U sa -P "$SA_PASSWORD" -b -Q "RESTORE VERIFYONLY FROM DISK = N'${DIR}/${file}' WITH CHECKSUM"
  gzip -f "${DIR}/${file}"
  find "$DIR" -name 'SmartQueue_*.bak.gz' -mtime +"$KEEP_DAYS" -delete
  echo "$(date -u) backup done: ${file}.gz"
}

[[ "${1:-}" == "--now" ]] && { backup; exit 0; }

while true; do
  now=$(date -u +%s)
  next=$(date -u -d "today ${HOUR}:00" +%s)
  (( next <= now )) && next=$(date -u -d "tomorrow ${HOUR}:00" +%s)
  sleep $(( next - now ))
  backup || echo "$(date -u) backup FAILED" >&2
done
