# Bug status

## Current state

`awaiting-user-confirmation`

## Active attempt

[Fix attempt 001](fix-attempt-001.md)

## Last updated

2026-10-03.

## Confirmation date

Not confirmed.

## Resolution summary

The planner's missing-hash error now names the source root, absolute root path,
and database. Forty-eight focused Release tests passed. Exclusion changes
require a successful rescan before planning uses them.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): diagnostic change and rescan regression
  in progress.
- [Fix attempt 001](fix-attempt-001.md): locally verified with 48 passing tests;
  awaiting user confirmation.

## State change log

- 2026-10-03: Bug opened and investigated from the reported CLI error.
- 2026-10-03: First attempt started after confirming the cause and exclusion
  persistence behavior.
- 2026-10-03: Verification completed. Release CLI rebuilt; bug remains open in
  `awaiting-user-confirmation` until the user confirms the diagnostic.
- 2026-10-03: User requested a commit and a Release rebuild. This authorizes
  delivery; the diagnostic still awaits confirmation from the user's environment.

## Notes

Preserve unrelated changes. No commits or pushes requested for this repair.

The subsequent user request authorizes committing this repair and rebuilding
Release. Preserve unrelated changes and do not push.
