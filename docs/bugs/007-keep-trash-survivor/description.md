# Bug description

## Title

KEEP trash survivor, review issue 6.

## Status

- awaiting-user-confirmation

## Reported symptoms

Imported KEEP plus TRASH plans cannot find a survivor because ListCompletedCopies excludes KEEP and the fresh database has no inventory.

## Expected behavior

Include completed KEEP destinations and revalidate each survivor on disk before trashing.

## Actual behavior

Imported KEEP plus TRASH plans cannot find a survivor because ListCompletedCopies excludes KEEP and the fresh database has no inventory.

## Reproduction details

The review imported into an unscanned DB; KEEP succeeded and TRASH conflicted despite the verified survivor.

## Affected area

Database, Executor, TrashSurvivorTests.

## Constraints

Implement the approved [review fixes plan](../../features/review-fixes/implementation-plan.md). Junction handling is excluded. Preserve existing records and unrelated changes.

## Open questions

None for implementation. User confirmation follows local verification.
