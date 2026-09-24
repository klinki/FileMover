# Initial findings

## Confirmed facts

- The attached screenshot shows almost black grid backgrounds and unreadable unselected file text in both panels.
- File cell text originally used a foreground binding to `MarkedToBrushConverter`. For unmarked rows, the converter returned `UnsetValue`, leaving the color to the DataGrid template.
- The [application resources](../../../src/BackupNormalizer.Ui/App.axaml) apply pale blue selected-row backgrounds in every theme.
- The converter uses the same dark red for marked text in every theme.

## Likely cause

The cell text color and selection colors do not adapt to the dark theme. The DataGrid surface remains close to black.

## Unknowns

- Whether the bundled DataGrid theme applies a separate foreground to selected rows after an explicit cell style is added.

## Reproduction status

Reproduced in the user screenshot. Local headless verification will check computed colors in both theme variants.

## Investigation update, 2026-09-24

Theme-specific resources set grid and text colors correctly. The DataGrid's own selection resource masked a same-named application theme resource, so the repair styles selected rows directly.
