# Per-drive source and target comparison

**Artifact:** `implementation-plan.md`
**Save path:** `C:\ai-workspace\FileMover\docs\features\per-drive-root-comparison\implementation-plan.md`

## Summary

Treat each database as an inventory for one drive by convention. It may contain several named roots, each with an absolute path. Neither a database nor a root has a permanent Canonical or Backup role. For each diff or automatic plan, the user selects a source database and root, and a target database and root. The source defines the desired file layout for that run. Either database can serve either purpose on another run.

## Implementation

- **Schema and inventory:** Remove root roles and the unused `CanonicalEntry` table. Keep root paths, writable status, filesystem details, scans, files, hashes, plans, and execution history. Scope every comparison to the two selected roots; never combine same named paths from other roots. After a complete scan, mark files not seen as missing. Mark incomplete scans as such and refuse to plan from them.
- **EF migration:** Regenerate a standard EF initial migration from the revised model. Current pre-release database files are disposable, as agreed: give a clear “recreate this database” error for the old schema, but never delete a database automatically. Keep `Migrate()` so later migrations apply normally.
- **CLI:** Use `--source-db`, `--source-root`, `--target-db`, and `--target-root` for both `diff` and `plan`. Permit both roots in one database. Replace the old canonical/old/new flags and remove root roles and the central inventory merge workflow. A diff reports source-only, target-only, changed, identical, and unverified files; matching size without valid hashes is unverified.
- **Plans and execution:** Store a generated plan in the target database, with its selected source root, source path hint, and target root. Identify each operation’s copy source as either the source root or the target root, so equal root IDs in separate databases remain unambiguous. Move files only within the target root; copy verified bytes from the source root when needed. Let `execute` and `verify` override stored paths when drives are remounted. Keep recoverable trash for target extras only when a full hash and a surviving target copy can be verified at execution. Missing, changed, or unavailable source bytes become conflicts.
- **UI and docs:** Keep the desktop UI’s live filesystem browsing and manual staging. Update its plan export/import to the new plan format without adding database browsing. Update the README and specification around the single source/target workflow.

## Verification

Test separate databases with equal root IDs, two disjoint roots in one database, reversed source/target direction, and isolation from other roots. Cover changed and unverified files, target-local moves, source copies, safe trash, stale files after rescan, unavailable or changed source bytes, and resume. Allow diffs of overlapping roots but reject automatic plans when their resolved paths overlap. Check fresh EF schema creation, UI plan round-trip, CLI database smoke test, the full suite, and the ARM publish.

## Assumptions

One drive per database is a convention, not a device-identity check. Comparison remains file-based; empty directories are not inventoried. Historical snapshots of the same physical root are for diff only. Old CLI flags, plan JSON, and pre-release database files need no compatibility layer.
