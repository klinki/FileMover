# Initial findings

## Confirmed facts

The review found inventory.db, inventory.db-wal, and inventory.db-shm without valid digests after scan/hash.

## Likely cause

Scanning an inventory DB under its own root inventories changing SQLite files, blocking hashing and planning.

## Unknowns

Environment confirmation is pending; native MFT I/O requires elevated Windows NTFS access.

## Reproduction status

Confirmed during the repository review. The current implementation still has the reported behavior.

## Evidence gathered

Code inspection and the earlier review reproductions. Planned regression checks: In-root DB scan/hash/plan succeeds; polluted entries become missing with stale hashes; ordinary .db content stays inventoried.

## Verification update, 2026-09-30

The exact active database and companions are excluded before metadata/error processing. Scan and hash retire old entries as missing and invalidate all their hashes. Recursive and MFT entries use the same exclusions. Regression checks passed in the final suite. User confirmation remains pending.
