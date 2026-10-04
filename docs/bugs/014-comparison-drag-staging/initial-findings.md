# Initial findings

## Confirmed facts

- `CanStage` permits two live panels only. Every drag handler uses this gate.
- Inventory rows carry root-relative paths. Live staging expects absolute paths
  under one shared base, so merely relaxing that gate would produce incorrect plans.
- Staged review and export currently build one-root plans. Inventory copies need
  distinct source and destination roots and `Source` scope.
- The existing native drag test is skipped because headless pointer delivery is
  not faithful. Drop event routing can be exercised directly in headless tests.

## Likely cause

Inventory staging was never connected to the live-panel drag implementation.

## Unknowns

Native drag feedback on the user's actual desktop.

## Reproduction status

The comparison rejection is established by the drag handlers and command gates.

## Evidence gathered

Reviewed `MainWindow.axaml.cs`, `MainViewModel`, inventory snapshots, `PlanDoc`,
and the existing execution/import and comparison tests.
