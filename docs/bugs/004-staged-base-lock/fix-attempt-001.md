# Fix attempt 001

## Attempt status

awaiting-user-confirmation

## Goal

Repair review issue 3: staged base lock.

## Relation to previous attempts

First implementation attempt, based on the completed repository review.

## Proposed change

Keep an applied base separate from editable text; lock base changes while staged operations exist; clear virtual folders with the queue.

## Risks

Preserve operation preconditions and existing inventory/execution history. Verify the relevant failure paths as well as success.

## Files and components

MainViewModel, MainWindow, UI staging tests.

## Verification plan

JSON and DB exports remain under A after edits or rejected Apply; clear/final removal unlocks the base and refreshes virtual folders.

## Implementation summary

Added AppliedBasePath and CanChangeBase. Staging and exports use the applied base, base changes are guarded and disabled while staged, and empty queues clear virtual folders and refresh panels.

## Test results

Focused regression run passed all 39 selected tests. After final review changes, the full suite passed 114 tests with 8 existing skips, 122 total. EF tooling reports no pending model changes. Git whitespace checks passed.

The full-suite run exposed an existing progress-test race caused by asynchronous Progress callbacks; the test now uses a synchronous IProgress observer. Production progress behavior was not changed.

## Outcome

Implementation is locally verified. User confirmation remains pending.

## Next step

Request user confirmation of the updated UI and fresh-database replay workflow.

## Remaining gaps

User confirmation remains pending.
