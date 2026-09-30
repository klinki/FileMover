# Bug description

## Title

Inventory database exclusion, review issue 7.

## Status

- awaiting-user-confirmation

## Reported symptoms

Scanning an inventory DB under its own root inventories changing SQLite files, blocking hashing and planning.

## Expected behavior

Exclude the active DB and exact companion paths from enumeration and hashing; retire polluted inventory entries and hashes.

## Actual behavior

Scanning an inventory DB under its own root inventories changing SQLite files, blocking hashing and planning.

## Reproduction details

The review found inventory.db, inventory.db-wal, and inventory.db-shm without valid digests after scan/hash.

## Affected area

Scanner, Database, inventory tests.

## Constraints

Implement the approved [review fixes plan](../../features/review-fixes/implementation-plan.md). Junction handling is excluded. Preserve existing records and unrelated changes.

## Open questions

None for implementation. User confirmation follows local verification.
