# Bug description

## Title

Analyze coverage freezes the desktop dialog.

## Status

`awaiting-user-confirmation`

## Reported symptoms

The coverage dialog stops responding after Analyze coverage is selected, despite
showing a progress bar.

## Expected behavior

Analysis and result preparation run in the background. The window and progress
animation remain responsive until the results are ready.

## Actual behavior

The database analysis uses a worker thread, but filtering, formatting location
details, and publishing each result run synchronously on the UI thread.

## Reproduction details

Add large inventory databases and select Analyze coverage. The existing D: and
G: inventories contain hundreds of thousands of regular files between them.

## Affected area

The desktop backup coverage view model and results grid.

## Constraints

Keep inventory reads read-only, preserve coverage classifications and locations,
and update bound UI state only on the UI thread.

## Open questions

User confirmation on the real inventory pair is still required after local checks.
