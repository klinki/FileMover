# Fix attempt 001

## Attempt status

awaiting-user-confirmation

## Goal

Repair review issue 4: staged operation order.

## Relation to previous attempts

First implementation attempt, based on the completed repository review.

## Proposed change

Preserve insertion order and sequential IDs through staging, export, import, and execution.

## Risks

Preserve operation preconditions and existing inventory/execution history. Verify the relevant failure paths as well as success.

## Files and components

PlanStaging, StagingTests.

## Verification plan

COPY then MOVE round-trip creates both desired destinations; generated MKDIR precedes its consumers.

## Implementation summary

BuildPlanDoc preserves staging order and sequential IDs. MKDIR stays before its consumers, and JSON/database import retain the sequence.

## Test results

Focused regression run passed all 39 selected tests. After final review changes, the full suite passed 114 tests with 8 existing skips, 122 total. EF tooling reports no pending model changes. Git whitespace checks passed.

The full-suite run exposed an existing progress-test race caused by asynchronous Progress callbacks; the test now uses a synchronous IProgress observer. Production progress behavior was not changed.

## Outcome

Implementation is locally verified. User confirmation remains pending.

## Next step

Request user confirmation of the updated UI and fresh-database replay workflow.

## Remaining gaps

User confirmation remains pending.
