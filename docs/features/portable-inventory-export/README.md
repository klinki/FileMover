# Export an inventory for another computer

```powershell
.\BackupNormalizer.exe db export --db .\d.db --output .\d-portable.db
```

Copy the resulting file to the other computer. It contains committed inventory data, including changes still held in the source WAL. The exported file needs no WAL or SHM companion. The command uses [Microsoft.Data.Sqlite's backup API](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup), checks integrity and foreign keys, and publishes the completed file without overwriting an existing destination.

The source may remain open. An export includes committed database state, so a running scan can appear as started or incomplete. Check the exported inventory with `status` before planning. SQLite backup temporarily blocks writers while copying; export is synchronous.

The export preserves the source schema, roots, scan history, diagnostics, hashes, plans, operation statuses, and journal checkpoints. It reads the source without migrating it. Opening an older exported database writable later applies normal application migrations.

Root paths and execution bindings retain their original meanings. Export does not remap drives or reset execution history. If a drive is mounted elsewhere, register its new root path and rescan as usual. For replaying an executed plan on another drive, use the existing fresh plan-import workflow.

The destination and its SQLite companion paths must be unused. Missing sources, existing destinations, source/companion collisions, corrupt databases, and integrity failures stop export. Only this operation's generated temporary files are cleaned up.

Verified on 2026-10-03 with nine focused Release checks passed and zero failures. Coverage includes an active WAL writer, exclusion of uncommitted changes, preservation of inventory data and diagnostics, historical schema export, collisions, invalid input cleanup, and CLI usage.
