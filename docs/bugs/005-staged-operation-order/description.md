# Bug description

## Title

Staged operation order, review issue 4.

## Status

- awaiting-user-confirmation

## Reported symptoms

BuildPlanDoc sorts MOVE before COPY, breaking a valid COPY followed by MOVE from the same source.

## Expected behavior

Preserve insertion order and sequential IDs through staging, export, import, and execution.

## Actual behavior

BuildPlanDoc sorts MOVE before COPY, breaking a valid COPY followed by MOVE from the same source.

## Reproduction details

The review confirmed that the move succeeded but the copy failed because its source was already moved.

## Affected area

PlanStaging, StagingTests.

## Constraints

Implement the approved [review fixes plan](../../features/review-fixes/implementation-plan.md). Junction handling is excluded. Preserve existing records and unrelated changes.

## Open questions

None for implementation. User confirmation follows local verification.
