# Bug status

## Current state

awaiting-user-confirmation

## Active attempt

[Fix attempt 001](fix-attempt-001.md)

## Last updated

2026-10-02

## Confirmation date

Pending.

## Resolution summary

Links are recorded separately from scan status, with immediate target metadata and non-fatal notes. Content processing skips links and linked parents. Explicit skip reasons survive plan export and import. Existing inventories migrate or load read-only without changing historical scan outcomes.

## Attempt history

- Attempt 001: implement the approved symlink inventory plan.
- Attempt 001 verification: 147 Release tests passed, 17 skipped, zero failures. Awaiting a user rescan with links.

## State change log

- 2026-10-02: bug opened; investigation completed; attempt 001 started.
- 2026-10-02: implementation completed, including migration and UI display.
- 2026-10-02: full Release verification completed. Real junction and synthetic MFT checks passed. File-symlink creation and elevated MFT were unavailable; nine new integration tests were skipped.
- 2026-10-02: state changed to awaiting-user-confirmation.

## Notes

User confirmation is required before marking the bug fixed.
