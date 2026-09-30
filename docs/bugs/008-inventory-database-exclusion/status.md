# Bug status

## Current state

- awaiting-user-confirmation

## Active attempt

[Fix attempt 001](fix-attempt-001.md).

## Last updated

2026-09-30

## Confirmation date

Pending.

## Resolution summary

The exact active database and companions are excluded before metadata/error processing. Scan and hash retire old entries as missing and invalidate all their hashes. Recursive and MFT entries use the same exclusions.

Full suite passed with 114 passed and 8 skipped. User confirmation remains pending.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): implementation started.
- [Fix attempt 001](fix-attempt-001.md): local verification passed; awaiting user confirmation.

## State change log

- 2026-09-30: bug opened from review issue 7; investigation confirmed; attempt 001 started.
- 2026-09-30: implementation and regression verification completed; awaiting-user-confirmation.

## Notes

User approved the implementation plan. No commit was requested.

- 2026-09-30: user requested committing the verified implementation. Environment confirmation remains pending.
