# Bug description

## Title

Dragging between inventory comparison panels does not stage operations.

## Status

`fixed`

## Reported symptoms

Files and folders could not be dragged from one comparison panel to the other.

## Expected behavior

Dropping into the opposite panel stages copies for review. Folders include their
indexed contents. Staging changes neither the inventory databases nor disk files.

## Behavior before the fix

Drag initiation, acceptance, and drop handling required two live filesystem panels.

## Reproduction details

Load two inventory databases, compare folders, and drag a file or folder across.

## Affected area

Desktop drag handlers, staged plans, export, and execution review.

## Constraints

Preserve live-folder move behavior, content checks, link exclusions, and the
separate source and destination roots. Preserve unrelated workspace changes.

## Open questions

None. The user confirmed native drag and drop in the rebuilt GUI on 2026-10-04.

## User confirmation

2026-10-04: The user confirmed dragging files and folders between comparison
panels stages the expected operations. The native desktop check is complete.
