# Fix attempt 001

## Attempt status

`awaiting-user-confirmation`

## Goal

Keep the dialog responsive through analysis, result preparation, and filtering.

## Relation to previous attempts

First attempt for this bug.

## Proposed change

Prepare result rows and summary counts on a worker thread. Publish a completed
result list through one property change. Run filter preparation asynchronously,
and disable the filter selector while work is running.

## Risks

Changing filters becomes asynchronous. Regression tests must await filtering and
verify that old results are cleared correctly when inputs change or analysis fails.

## Files and components

- [Coverage view model](../../../src/BackupNormalizer.Ui/ViewModels/BackupCoverageViewModel.cs)
- [Coverage window](../../../src/BackupNormalizer.Ui/Views/BackupCoverageWindow.axaml)
- [Coverage UI tests](../../../tests/BackupNormalizer.Tests/UiBackupCoverageTests.cs)

## Verification plan

Check core coverage classifications, filter behavior, error recovery, worker
thread execution, UI-thread publication, and responsiveness while large results
are prepared. Build the updated GUI for the user's real-inventory check.

## Implementation summary

Preparation and filtering run on workers. Completed results replace the grid's
items in one update. Summary counts are cached, and obsolete work is discarded.

## Test results

Nine focused coverage tests passed. The combined coverage, inventory staging,
database planning, and execution run passed all 55 tests.

## Outcome

Local verification passed. Awaiting the user's real-inventory check.

## Next step

Commit the coverage change separately and open the rebuilt GUI for retesting.

## Remaining gaps

The bug stays open until the user confirms the real dialog is responsive.

## Verified implementation update

Result rows, location strings, and summary counts are prepared inside `Task.Run`.
The grid receives the completed list in one property change. Filtering also runs
on a worker, and stale work is discarded when inputs change. The filter selector
is disabled during work.

`dotnet test` with `UiBackupCoverageTests|BackupCoverageTests` passed all nine tests.
The large-result regression checks worker-thread enumeration, responsive UI
dispatch, one publication on the UI thread, and asynchronous filtering. Release
build passed with existing Avalonia constructor warnings. User confirmation is
pending.
