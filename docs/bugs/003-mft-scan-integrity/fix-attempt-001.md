# Fix attempt 001

## Attempt status

awaiting-user-confirmation

## Goal

Repair review issue 1: mft scan integrity.

## Relation to previous attempts

First implementation attempt, based on the completed repository review.

## Proposed change

Decode the native header, walk returned record numbers downward, and fail scans on unexpected enumeration errors.

## Risks

Preserve operation preconditions and existing inventory/execution history. Verify the relevant failure paths as well as success.

## Files and components

MFT decoder and enumerator, Scanner, MftTests.

## Verification plan

Synthetic output buffers and record sequences; failed and incomplete scan preservation; elevated NTFS comparison when available.

## Implementation summary

Decoded and validated the native header and payload; read records downward using returned numbers; retained directory ancestry; distinguished extension metadata from malformed records; propagated failures to the scanner with --mft off retry guidance.

## Test results

Focused regression run passed all 39 selected tests. After final review changes, the full suite passed 114 tests with 8 existing skips, 122 total. EF tooling reports no pending model changes. Git whitespace checks passed.

The full-suite run exposed an existing progress-test race caused by asynchronous Progress callbacks; the test now uses a synchronous IProgress observer. Production progress behavior was not changed.

## Outcome

Implementation is locally verified. Native NTFS comparison is pending because the current Windows token is not elevated. User confirmation remains pending.

## Next step

Request user confirmation of the updated UI and fresh-database replay workflow.

## Remaining gaps

Native NTFS comparison is pending because the current Windows token is not elevated. User confirmation remains pending.
