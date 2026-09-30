# Bug description

## Title

Execution root binding, review issue 5.

## Status

- awaiting-user-confirmation

## Reported symptoms

Completed statuses are reused when execution is remapped to another drive, reporting completion without applying operations there.

## Expected behavior

Persist the effective execution roots before the first operation and reject later mismatches; support JSON replay through a fresh database.

## Actual behavior

Completed statuses are reused when execution is remapped to another drive, reporting completion without applying operations there.

## Reproduction details

The review executed on A, then overrode the target to B; B remained untouched while execution reported completion.

## Affected area

EF model and migration, Database, Executor, CLI verification, replay tests.

## Constraints

Implement the approved [review fixes plan](../../features/review-fixes/implementation-plan.md). Junction handling is excluded. Preserve existing records and unrelated changes.

## Open questions

None for implementation. User confirmation follows local verification.
