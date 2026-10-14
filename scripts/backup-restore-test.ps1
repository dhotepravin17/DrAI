<#
.SYNOPSIS
  Restore drill (PRD §35): back up the database, verify the backup, restore it as a copy, compare row counts, drop the copy.

.EXAMPLE
  ./scripts/backup-restore-test.ps1
  ./scripts/backup-restore-test.ps1 -Server "sql01" -Database SmartQueue -BackupDirectory "D:\Backups"
#>
param(
    [string]$Server = '(localdb)\SmartQueue',
    [string]$Database = 'SmartQueue',
    [string]$BackupDirectory = $env:TEMP,
    [switch]$KeepCopy
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { throw 'sqlcmd was not found on the PATH.' }
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw 'Database name may contain only letters, digits and underscores.' }

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$copy = "${Database}_RestoreTest_$stamp"
$backup = Join-Path $BackupDirectory "${Database}_$stamp.bak"

function Invoke-Sql([string]$query, [string]$db = 'master') {
    $output = & sqlcmd -S $Server -d $db -E -b -h -1 -W -s '|' -Q "SET NOCOUNT ON; $query"
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $output" }
    return $output
}

function Get-RowCounts([string]$db) {
    $rows = Invoke-Sql "SELECT t.name, SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) GROUP BY t.name ORDER BY t.name" $db
    $counts = @{}
    foreach ($line in $rows) {
        $parts = "$line".Split('|')
        if ($parts.Count -eq 2) { $counts[$parts[0].Trim()] = [long]$parts[1].Trim() }
    }
    return $counts
}

$started = Get-Date
Write-Host "Backing up $Database on $Server to $backup"
Invoke-Sql "BACKUP DATABASE [$Database] TO DISK = N'$backup' WITH INIT, CHECKSUM, COPY_ONLY" | Out-Null
$before = Get-RowCounts $Database

Write-Host 'Verifying backup'
Invoke-Sql "RESTORE VERIFYONLY FROM DISK = N'$backup' WITH CHECKSUM" | Out-Null

Write-Host "Restoring as $copy"
$fileList = Invoke-Sql "RESTORE FILELISTONLY FROM DISK = N'$backup'"
$moves = @()
$dataDir = (Invoke-Sql "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400))").Trim()
if (-not $dataDir) { $dataDir = $BackupDirectory }
foreach ($line in $fileList) {
    $parts = "$line".Split('|')
    if ($parts.Count -lt 3) { continue }
    $logical = $parts[0].Trim()
    $ext = if ($parts[2].Trim() -eq 'L') { 'ldf' } else { 'mdf' }
    $moves += "MOVE N'$logical' TO N'$(Join-Path $dataDir "${copy}_$logical.$ext")'"
}
Invoke-Sql "RESTORE DATABASE [$copy] FROM DISK = N'$backup' WITH $($moves -join ', '), CHECKSUM, RECOVERY" | Out-Null

Write-Host 'Comparing row counts'
$after = Get-RowCounts $copy
$mismatches = @()
foreach ($table in $before.Keys) {
    if ($after[$table] -ne $before[$table]) { $mismatches += "$table : $($before[$table]) -> $($after[$table])" }
}
Invoke-Sql "DBCC CHECKDB ([$copy]) WITH NO_INFOMSGS" | Out-Null

if (-not $KeepCopy) {
    Invoke-Sql "ALTER DATABASE [$copy] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$copy]" | Out-Null
    Remove-Item $backup -ErrorAction SilentlyContinue
}

$elapsed = (Get-Date) - $started
Write-Host ("Tables checked: {0}. Elapsed: {1:mm\:ss}." -f $before.Count, $elapsed)
if ($mismatches.Count -gt 0) {
    Write-Error ("Row counts differ after restore:`n" + ($mismatches -join "`n"))
    exit 1
}
Write-Host 'Restore drill passed.'
