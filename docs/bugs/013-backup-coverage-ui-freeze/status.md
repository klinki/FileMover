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

Investigated the result-formatting and per-row notification work that runs on
the UI thread after the background database analysis.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): worker-thread preparation and batch
  publication planned.

## State change log

- 2026-10-04: User reported a frozen Analyze coverage dialog. Bug opened.
- 2026-10-04: Investigation confirmed UI-thread formatting and per-row grid
  notifications. First attempt started.

## Notes

Preserve unrelated changes. No commit or push requested for this repair.

- 2026-10-04: The user subsequently requested a separate commit for each change.

## Verification update

- 2026-10-04: Preparation and filtering now run on a worker. Nine focused coverage
  tests passed, including a 50,000-result UI responsiveness regression. GUI build
  passed. Awaiting the user's check with their inventories.
