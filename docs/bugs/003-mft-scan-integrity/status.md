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

Decoded and validated the native header and payload; read records downward using returned numbers; retained directory ancestry; distinguished extension metadata from malformed records; propagated failures to the scanner with --mft off retry guidance.

Full suite passed with 114 passed and 8 skipped. Native NTFS comparison is pending because the current Windows token is not elevated. User confirmation remains pending.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): implementation started.
- [Fix attempt 001](fix-attempt-001.md): local verification passed; awaiting user confirmation.

## State change log

- 2026-09-30: bug opened from review issue 1; investigation confirmed; attempt 001 started.
- 2026-09-30: implementation and regression verification completed; awaiting-user-confirmation.

## Notes

User approved the implementation plan. No commit was requested.

- 2026-09-30: user requested committing the verified implementation. Environment confirmation remains pending.
