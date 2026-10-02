# Bug description

## Title

Symlinks make successful inventories incomplete.

## Status

awaiting-user-confirmation

## Reported symptoms

File links are stored as unsupported entries and counted as scan errors. Directory links are omitted. Broken links can fail before classification.

## Expected behavior

Record links and immediate target paths without following them. Skip content processing and report skipped operations. Missing targets or unreadable link metadata must not fail a scan.

## Actual behavior

The scanner reads file length before handling reparse points, increments errors for file links, and prevents complete scans from retiring missing entries. Inventory panels interpret unsupported entries as scan errors.

## Reproduction details

Scan a root containing a regular file and a file symlink, or a dangling file symlink. The scan returns errors and is marked incomplete.

## Affected area

Core scanning, MFT enumeration, inventory persistence, hashing, staging, planning, execution, CLI reporting, and database inventory panels.

## Constraints

Preserve unrelated changes. Do not follow links, copy external targets, recreate links, commit, or push. Preserve old read-only inventories and historical scan statuses.

## Open questions

None. The user approved the [implementation plan](../../features/symlink-inventory/implementation-plan.md).
