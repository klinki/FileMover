# Bug status

## Current state

`fixed`

## Confirmed attempt

[Fix attempt 001](fix-attempt-001.md)

## Last updated

2026-10-04.

## Confirmation date

2026-10-04.

## Resolution summary

Inventory drops and F5 stage recursive copies from snapshot metadata, retaining
the selected source and target roots for export and execution review.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): inventory copy staging implemented,
  verified locally, and confirmed by the user.

## State change log

- 2026-10-04: User reported comparison drag failure. Bug opened and investigated.
- 2026-10-04: First attempt started.
- 2026-10-04: Routed drops, offline copies, metadata checks, export, and temporary
  execution verified. All 55 combined focused tests passed. At that point, the
  attempt awaited user retesting.
- 2026-10-04: User confirmed both fixes work in the rebuilt GUI. Comparison drag
  staging marked fixed. Implementation committed as `1dc154b`.

## Notes

The user requested a separate implementation commit for each repair. Both commits
are complete. Unrelated workspace changes were preserved.
