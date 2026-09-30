# Fix attempt 001

## Attempt status

awaiting-user-confirmation

## Goal

Repair review issue 6: keep trash survivor.

## Relation to previous attempts

First implementation attempt, based on the completed repository review.

## Proposed change

Include completed KEEP destinations and revalidate each survivor on disk before trashing.

## Risks

Preserve operation preconditions and existing inventory/execution history. Verify the relevant failure paths as well as success.

## Files and components

Database, Executor, TrashSurvivorTests.

## Verification plan

Fresh imported KEEP/TRASH succeeds; changed, missing, or self-referencing survivors refuse trash.

## Implementation summary

Completed KEEP destinations are survivor candidates. Normalized path comparison excludes the victim, and survivor size/hash are checked on disk before TRASH.

## Test results

Focused regression run passed all 39 selected tests. After final review changes, the full suite passed 114 tests with 8 existing skips, 122 total. EF tooling reports no pending model changes. Git whitespace checks passed.

The full-suite run exposed an existing progress-test race caused by asynchronous Progress callbacks; the test now uses a synchronous IProgress observer. Production progress behavior was not changed.

## Outcome

Implementation is locally verified. User confirmation remains pending.

## Next step

Request user confirmation of the updated UI and fresh-database replay workflow.

## Remaining gaps

User confirmation remains pending.
