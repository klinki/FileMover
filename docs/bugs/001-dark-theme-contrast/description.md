# Dark theme contrast

## Status

- Fixed

## Reported symptoms

In the dark system theme, the main file panels are nearly black and unselected file names, extensions, sizes, and dates are hard to read. The attached screenshot shows folder icons without visible names in most rows.

## Expected behavior

Both file panels and the staged operations window should have readable text and clear selection and marked-file colors in light and dark themes.

## Reproduction details

Run the Avalonia UI on Windows with the system dark theme, then browse a directory containing files and folders.

## Affected area

The [application theme](../../../src/BackupNormalizer.Ui/App.axaml) and [file panels](../../../src/BackupNormalizer.Ui/Views/MainWindow.axaml).

## Constraints

Keep the application following the system theme and preserve the existing light-theme selection colors.
