# Fix attempt 001

## Attempt status

Fixed after user confirmation on 2026-09-24.

## Goal

Make file names and metadata readable in dark mode while retaining a clear light mode.

## Relation to previous attempts

First attempt.

## Proposed change

- Add theme-specific grid surface, file text, marked text, and selection brushes.
- Bind the file row text to theme-aware styles instead of returning an unset foreground for unmarked rows.
- Add focused headless checks for computed row colors in light and dark variants.

## Risks

DataGrid styling can override local text styles or selection brushes. Verify actual rendered controls in both variants.

## Files and components

The [application theme](../../../src/BackupNormalizer.Ui/App.axaml), [main window](../../../src/BackupNormalizer.Ui/Views/MainWindow.axaml), and [headless panel tests](../../../tests/BackupNormalizer.Tests/HeadlessPanelTests.cs).

## Verification plan

Build the UI, run focused headless tests, and inspect actual row foreground and background brushes under both theme variants.

## Implementation summary

Added light and dark brushes for the window, menu, inputs, grids, column headers, row text, marked text, and selected rows. File cells now bind the `marked` style class to `IsMarked`. Removed the converter that returned an unset foreground for unmarked rows.

## Test results

The UI and test projects built in a separate output directory because the running UI locked its normal Debug output files. The Release UI build succeeded. Seven focused headless tests passed. The new test reads computed grid, column header, unmarked text, marked text, and selected-row colors under both `Dark` and `Light` variants.

## Outcome

Local verification passed. The user opened the Release build and confirmed that the dark theme is readable.

## Remaining gaps

None reported.
