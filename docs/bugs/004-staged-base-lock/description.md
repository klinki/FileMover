# Bug description

## Title

Staged base lock, review issue 3.

## Status

- awaiting-user-confirmation

## Reported symptoms

Changing BasePath after staging exports old relative operations against a different directory.

## Expected behavior

Keep an applied base separate from editable text; lock base changes while staged operations exist; clear virtual folders with the queue.

## Actual behavior

Changing BasePath after staging exports old relative operations against a different directory.

## Reproduction details

The review staged under A, applied B, then executed an export that moved B's file while leaving A unchanged.

## Affected area

MainViewModel, MainWindow, UI staging tests.

## Constraints

Implement the approved [review fixes plan](../../features/review-fixes/implementation-plan.md). Junction handling is excluded. Preserve existing records and unrelated changes.

## Open questions

None for implementation. User confirmation follows local verification.
