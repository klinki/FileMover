# Bug status

## Current state

`awaiting-user-confirmation`

## Active attempt

[Fix attempt 001](fix-attempt-001.md)

## Last updated

2026-10-04.

## Confirmation date

Not confirmed.

## Resolution summary

Inventory drops and F5 stage recursive copies from snapshot metadata, retaining
the selected source and target roots for export and execution review.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): inventory copy staging planned.

## State change log

- 2026-10-04: User reported comparison drag failure. Bug opened and investigated.
- 2026-10-04: First attempt started.
- 2026-10-04: Routed drops, offline copies, metadata checks, export, and temporary
  execution verified. All 55 combined focused tests passed. Awaiting user retest.

## Notes

No commit or push requested for this repair.

- 2026-10-04: The user subsequently requested a separate commit for each change.
