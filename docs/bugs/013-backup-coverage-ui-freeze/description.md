# Bug description

## Title

Analyze coverage freezes the desktop dialog.

## Status

`fixed`

## Reported symptoms

The coverage dialog stopped responding after Analyze coverage was selected, despite
showing a progress bar.

## Expected behavior

Analysis and result preparation run in the background. The window and progress
animation remain responsive until the results are ready.

## Behavior before the fix

The database analysis used a worker thread, but filtering, formatting location
details, and publishing each result ran synchronously on the UI thread.

## Reproduction details

Add large inventory databases and select Analyze coverage. The existing D: and
G: inventories contain hundreds of thousands of regular files between them.

## Affected area

The desktop backup coverage view model and results grid.

## Constraints

Keep inventory reads read-only, preserve coverage classifications and locations,
and update bound UI state only on the UI thread.

## Open questions

None. The user confirmed responsiveness with the real inventory pair on 2026-10-04.

## User confirmation

2026-10-04: The user confirmed both fixes work in the rebuilt GUI. The required
real-inventory check is complete.
