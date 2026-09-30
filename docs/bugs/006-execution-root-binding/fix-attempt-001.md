# Fix attempt 001

## Attempt status

awaiting-user-confirmation

## Goal

Repair review issue 5: execution root binding.

## Relation to previous attempts

First implementation attempt, based on the completed repository review.

## Proposed change

Persist the effective execution roots before the first operation and reject later mismatches; support JSON replay through a fresh database.

## Risks

Preserve operation preconditions and existing inventory/execution history. Verify the relevant failure paths as well as success.

## Files and components

EF model and migration, Database, Executor, CLI verification, replay tests.

## Verification plan

First-run overrides, restart/resume, mismatched root rejection without state changes, fresh JSON replay, drift conflicts, and EF upgrade preservation.

## Implementation summary

Added the standard BindExecutionRoots EF migration and nullable execution paths. A transaction binds effective paths before operations start. Execution rejects mismatches and unbound historical attempts; retry and verification default to bound paths. Fresh import preserves the JSON format and creates planned statuses.

## Test results

Focused regression run passed all 39 selected tests. After final review changes, the full suite passed 114 tests with 8 existing skips, 122 total. EF tooling reports no pending model changes. Git whitespace checks passed.

The full-suite run exposed an existing progress-test race caused by asynchronous Progress callbacks; the test now uses a synchronous IProgress observer. Production progress behavior was not changed.

## Outcome

Implementation is locally verified. User confirmation remains pending.

## Next step

Request user confirmation of the updated UI and fresh-database replay workflow.

## Remaining gaps

User confirmation remains pending.
