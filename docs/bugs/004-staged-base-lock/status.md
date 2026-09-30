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

Added AppliedBasePath and CanChangeBase. Staging and exports use the applied base, base changes are guarded and disabled while staged, and empty queues clear virtual folders and refresh panels.

Full suite passed with 114 passed and 8 skipped. User confirmation remains pending.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): implementation started.
- [Fix attempt 001](fix-attempt-001.md): local verification passed; awaiting user confirmation.

## State change log

- 2026-09-30: bug opened from review issue 3; investigation confirmed; attempt 001 started.
- 2026-09-30: implementation and regression verification completed; awaiting-user-confirmation.

## Notes

User approved the implementation plan. No commit was requested.

- 2026-09-30: user requested committing the verified implementation. Environment confirmation remains pending.
