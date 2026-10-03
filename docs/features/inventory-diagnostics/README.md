# Inventory status and scan errors

```powershell
.\BackupNormalizer.exe status d --db .\d.db
.\BackupNormalizer.exe status --db .\d.db --json
.\BackupNormalizer.exe scan errors d --db .\d.db
.\BackupNormalizer.exe scan errors d --scan 2 --db .\d.db --json
```

`status` reads the inventory without migrating it or accessing its recorded roots. It reports the latest scan and last successful scan, enumeration mode, entry/error counts, recorded failure paths, usable hashes, regular files needing hashes, skipped links, and the stored USN checkpoint. The checkpoint is validated against the live journal only during scanning. A fallback reason describes what happened during the recorded scan.

Status exits with code `0` when all selected roots have a complete latest scan, no inventory entry errors, and usable hashes for their regular files. Code `3` means those content-planning prerequisites are incomplete. Code `2` reports invalid arguments or a database error. A writable target, disjoint roots, and an unused plan ID are still checked by planning itself.

`scan errors` defaults to the latest scan for the selected root. `--scan` selects an older scan belonging to that root. Error paths and messages are retained after failures, incremental rollback, and later successful rescans. Text output sanitizes terminal control characters; JSON retains the original data with JSON escaping.

Writable inventories migrate automatically. Historical scans retain their original status and have unknown diagnostic counts. Read-only older inventories are inspected through legacy projections without migration. A rescan records future diagnostics; it cannot recover failure paths that an old application never stored. Non-fatal link notes remain separate from scan errors.

Verified on 2026-10-03 with 51 focused Release tests passed, ten permission-dependent skips, and zero failures. Coverage includes persistence, fatal failures, retry rollback, readiness, link exclusion, JSON output, legacy read-only compatibility, and migration. A pooling-related fixture lock was corrected by disabling pooling for the legacy fixture before the passing run.
