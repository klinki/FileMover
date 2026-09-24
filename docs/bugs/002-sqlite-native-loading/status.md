# Bug status

## Current state

- Awaiting user confirmation

## Active attempt

[Fix attempt 001](fix-attempt-001.md)

## Last updated

2026-09-24

## Confirmation date

Awaiting user confirmation.

## Resolution summary

EF Core's SQLite provider passes Windows tests and publishes an ARM32 musl native library. Actual QNAP execution remains unverified.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): implemented and locally verified; QNAP confirmation pending.

## State change log

- 2026-09-24: Bug opened from the Windows native-library failure and package review.
- 2026-09-24: Fix attempt 001 started.
- 2026-09-24: Regular package and QNAP image configuration applied.
- 2026-09-24: Full Windows suite passed, CLI database smoke test passed, and ARM32 musl publish completed.
- 2026-09-24: Awaiting the QNAP acceptance test on the actual NAS.
- 2026-09-24: EF Core data access replaced direct SQL; an EF-generated initial migration creates new database schemas.
