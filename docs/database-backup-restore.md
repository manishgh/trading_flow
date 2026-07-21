# SQLite Backup and Restore

## Runtime Policy

TradingFlow creates one immutable SQLite online backup for each New York operational date. The scheduled run is `20:30 America/New_York`, after the configured extended-hours session. On startup, the service verifies or creates the most recently completed operational date, then waits for the next scheduled run.

Each backup is:

- created through SQLite's online backup API while the application may continue reading and writing;
- checked with `PRAGMA integrity_check` before publication;
- flushed to durable storage before an atomic rename;
- stored with a SHA-256 manifest;
- never overwritten or pruned by the application.

Default paths:

```text
TRADINGFLOW_DATA_ROOT/tradingflow.db
TRADINGFLOW_BACKUP_ROOT/YYYY/MM/tradingflow-YYYY-MM-DD.db
TRADINGFLOW_BACKUP_ROOT/YYYY/MM/tradingflow-YYYY-MM-DD.db.manifest.json
```

`TRADINGFLOW_BACKUP_ROOT` defaults to `TRADINGFLOW_DATA_ROOT/backups` for local development. Production must bind it to storage with a failure domain separate from the operational SQLite disk and archive it to Azure Blob Storage. Operational and journal retention is indefinite.

Optional schedule settings use normal ASP.NET Core configuration binding:

```text
DatabaseBackup__LocalTime=20:30
DatabaseBackup__MarketTimeZone=America/New_York
```

## Manual Backup

The operational date is explicit so an operator cannot accidentally label a backup with the host's local date:

```powershell
dotnet run --project src\TradingFlow.Cli\TradingFlow.Cli.csproj -- database-backup `
  --database C:\tradingflow\state\tradingflow.db `
  --backup-root D:\tradingflow-backups `
  --operational-date 2026-07-21
```

The command is idempotent. If that date already exists, it validates the retained database against the existing immutable manifest without replacing either file. A manifest is reconstructed only when a process stopped after publishing a valid database but before publishing its sidecar; an existing mismatched manifest fails closed.

## Restore To Scratch

Restore deliberately refuses to overwrite a database, WAL, or shared-memory file. Stop the scratch host and choose an empty destination:

```powershell
dotnet run --project src\TradingFlow.Cli\TradingFlow.Cli.csproj -- database-restore `
  --backup D:\tradingflow-backups\2026\07\tradingflow-2026-07-21.db `
  --destination C:\tradingflow\restore-drill\tradingflow.db
```

The command verifies the source database, verifies its manifest hash, copies with write-through semantics, verifies the copy, and atomically publishes the destination.

Point a scratch Web instance at the restored directory and verify startup:

```powershell
$env:TRADINGFLOW_DATA_ROOT = 'C:\tradingflow\restore-drill'
$env:TRADINGFLOW_CACHE_ROOT = 'C:\tradingflow\restore-drill-cache'
dotnet run --project src\TradingFlow.Web\TradingFlow.Web.csproj --urls http://127.0.0.1:53150
```

Check `/health`, `/Paper`, and `/TradeDesk`. The quarterly TST-08 drill additionally compares journal row counts and sampled order chains before the backup is approved for recovery use.

## Production Recovery

Do not restore over a running database. The production recovery sequence is:

1. Stop the sole SQLite writer and verify no process has the database open.
2. Restore the selected backup to a new empty staging path.
3. Run the scratch startup and TST-08 validation.
4. Preserve the failed operational database and its `-wal`/`-shm` files for forensics.
5. Switch the operational volume binding to the validated restored database.
6. Start one writer, reconcile broker orders and positions, and keep entries blocked until reconciliation succeeds.

Automated overwrite is intentionally unsupported.
