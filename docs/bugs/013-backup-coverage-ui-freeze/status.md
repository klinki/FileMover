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

Result preparation and filtering run on workers, and completed results update
the grid in one batch. The user confirmed coverage analysis remains responsive.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): worker-thread preparation and batch
  publication implemented, verified locally, and confirmed by the user.

## State change log

- 2026-10-04: User reported a frozen Analyze coverage dialog. Bug opened.
- 2026-10-04: Investigation confirmed UI-thread formatting and per-row grid
  notifications. First attempt started.
- 2026-10-04: User confirmed both fixes work in the rebuilt GUI. Coverage freeze
  marked fixed. Implementation committed as `2839f73`.

## Notes

The user requested a separate implementation commit for each repair. Both commits
are complete. Unrelated workspace changes were preserved.

## Verification update

- 2026-10-04: Preparation and filtering now run on a worker. Nine focused coverage
  tests passed, including a 50,000-result UI responsiveness regression. GUI build
  passed. The subsequent user check with their inventories confirmed the fix.
