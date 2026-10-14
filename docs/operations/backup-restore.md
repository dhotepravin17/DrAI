# Backup and restore

Targets from PRD §35: recovery point objective (RPO) 15 minutes, recovery time objective (RTO) 4 hours, and a
restore test every quarter.

## Production (Azure SQL Database)

Azure SQL takes automatic full, differential and transaction-log backups, so point-in-time restore is available
with an RPO of about 5–10 minutes. That meets the 15-minute target with no extra work.

| Setting | Value |
|---|---|
| Point-in-time retention | 14 days or more |
| Long-term retention | Weekly backups kept 12 weeks; monthly kept 12 months (adjust after OQ-03) |
| Backup storage redundancy | Geo-redundant, so a region outage can be restored in the paired region |
| Region | Central India preferred (PRD §6.2; confirm data residency with legal) |

### Restore procedure

1. Pick the restore point: the last good time before the incident (IST → convert to UTC).
2. Restore to a **new** database: Azure portal → SQL database → *Restore*, or
   `az sql db restore --dest-name SmartQueue-restore-<date> --time <UTC ISO time> ...`.
3. Run the integrity checks below against the restored database.
4. Stop the API (or put it in maintenance), swap connection strings to the restored database, and start it.
   Do not run migrations on startup in production (`Bootstrap__ApplyMigrationsOnStartup=false`).
5. Record the drill or incident: start time, restore point, time to service, and any data gap.

### Integrity checks after a restore

```sql
SELECT COUNT(*) FROM Doctors;
SELECT COUNT(*) FROM Appointments;
SELECT MAX(CreatedAt) FROM Appointments;   -- should be close to the chosen restore point
SELECT COUNT(*) FROM OutboxMessages WHERE Status = 'Pending';   -- the worker sends these after start-up
-- No duplicate tokens (must return no rows):
SELECT QueueSessionId, TokenNumber FROM Appointments GROUP BY QueueSessionId, TokenNumber HAVING COUNT(*) > 1;
```

After the API starts, check `/health` and that the admin overview loads.

Pending outbox messages older than their expiry are marked failed rather than sent, so patients are not told
"it's your turn" hours late after a restore.

## Restore drill on SQL Server (local or self-hosted)

`scripts/backup-restore-test.ps1` runs a complete drill: it backs up the database, verifies the backup, restores
it under a new name, compares row counts table by table, and drops the copy.

```powershell
./scripts/backup-restore-test.ps1                                   # LocalDB (localdb)\SmartQueue, database SmartQueue
./scripts/backup-restore-test.ps1 -Server "sql01" -Database SmartQueue -BackupDirectory "D:\Backups"
```

It needs `sqlcmd` on the path and exits non-zero if any table's row count differs. Run it each quarter and
keep the output with the drill record.
