# User-authorized inventory status override

## Request

On 2026-10-02, the user explicitly requested a new build and asked to mark the latest scan in [d.db](G:/__drive_diff/d.db) complete so planning could proceed. This is a manual override of the two reported scan errors, separate from verification of the CLI progress and diagnostic fixes.

## Before the change

- Root `d` points to `D:\` and is writable.
- Latest scan #2 was `Incomplete`, with a recorded completion timestamp.
- All 176,440 inventory entries were `Ok` and had `LastSeenScanId = 2`; no entry errors were stored.
- All 175,508 cached SHA256 hashes were `Ok`.
- The database contained zero plans, and its WAL had no pending data.

## Backup

Created an [integrity-checked SQLite backup](../../../publish/database-backups/d-before-status-override-2026-10-02-26a2c255.db) before modification. The adjacent [audit manifest](../../../publish/database-backups/d-before-status-override-2026-10-02-26a2c255.json) records the original and updated scan rows. These recovery artifacts are in the ignored publish directory.

## Applied change

Updated exactly one row in an immediate SQLite transaction after checking that the scan and inventory still matched the inspected state:

```sql
UPDATE Scan
SET Status = 'Completed'
WHERE Id = 2 AND StorageRootId = 'd' AND Status = 'Incomplete';
```

The database write required sandbox escalation because the user's database is outside the workspace. The explicitly requested override was approved and applied.

## Verification

- SQLite integrity check returned `ok`.
- Read-only verification confirmed that the latest target scan is `Completed`.
- Inventory and hash counts are preserved; scan #1 remains `Incomplete`.
- Fresh Release rebuild succeeded with zero errors and four existing warnings; CLI startup succeeded.
- No source changes were required for this override. The prior Release suite passed with 169 tests passed and 17 skipped.

## Limits

The two underlying filesystem failures have not been repaired. The change authorizes planning against the existing inventory at the user's request. Future scans retain their normal error handling and can mark the root incomplete again. Existing bug statuses remain awaiting user confirmation.
