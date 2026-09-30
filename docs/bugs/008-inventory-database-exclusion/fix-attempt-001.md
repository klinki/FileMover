# Fix attempt 001

## Attempt status

awaiting-user-confirmation

## Goal

Repair review issue 7: inventory database exclusion.

## Relation to previous attempts

First implementation attempt, based on the completed repository review.

## Proposed change

Exclude the active DB and exact companion paths from enumeration and hashing; retire polluted inventory entries and hashes.

## Risks

Preserve operation preconditions and existing inventory/execution history. Verify the relevant failure paths as well as success.

## Files and components

Scanner, Database, inventory tests.

## Verification plan

In-root DB scan/hash/plan succeeds; polluted entries become missing with stale hashes; ordinary .db content stays inventoried.

## Implementation summary

The exact active database and companions are excluded before metadata/error processing. Scan and hash retire old entries as missing and invalidate all their hashes. Recursive and MFT entries use the same exclusions.

## Test results

Focused regression run passed all 39 selected tests. After final review changes, the full suite passed 114 tests with 8 existing skips, 122 total. EF tooling reports no pending model changes. Git whitespace checks passed.

The full-suite run exposed an existing progress-test race caused by asynchronous Progress callbacks; the test now uses a synchronous IProgress observer. Production progress behavior was not changed.

## Outcome

Implementation is locally verified. User confirmation remains pending.

## Next step

Request user confirmation of the updated UI and fresh-database replay workflow.

## Remaining gaps

User confirmation remains pending.
