# Inventory diagnostics plan

Approved on 2026-10-03 as item 2 of the inventory reliability work.

- Persist each scan's enumeration mode, entry/error counts, USN fallback reason, and timestamped failure paths/messages through an EF migration.
- Retain diagnostics after incremental rollback and fatal failures. Non-fatal link notes remain separate.
- Add read-only `status [rootId]` and `scan errors <rootId> [--scan ID]` commands with text and JSON output. Show latest and last successful scans, planning readiness, usable/missing hashes, skipped links, and checkpoint availability.
- Support legacy read-only databases without migration. Unknown historical diagnostics must be reported as unavailable, not zero errors.
- Verify scan outcomes, rollback, migration, legacy reads, readiness, and CLI output. Commit this item separately.
