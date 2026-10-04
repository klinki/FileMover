# Initial findings

## Confirmed facts

- The coverage analyzer already runs through `Task.Run`.
- After awaiting it, the view model runs `ApplyFilter` on the caller's UI context.
- `ApplyFilter` formats every visible row and its location details, then calls
  `ObservableCollection.Add` for each result. Each add notifies the bound grid.
- Filter changes repeat the same synchronous formatting and collection updates.

## Likely cause

Large results block the UI while the view model prepares rows and the grid
processes thousands of individual collection notifications.

## Unknowns

The exact elapsed time on the user's D: and G: inventories has not been measured.

## Reproduction status

The blocking path is confirmed from code. Add a large-result regression that
checks worker-thread row preparation and one batch publication on the UI thread.

## Evidence gathered

Reviewed the [coverage view model](../../../src/BackupNormalizer.Ui/ViewModels/BackupCoverageViewModel.cs),
[coverage window](../../../src/BackupNormalizer.Ui/Views/BackupCoverageWindow.axaml),
and [core analyzer](../../../src/BackupNormalizer.Core/BackupCoverage.cs).
