# Bug description

## Title

Dragging between inventory comparison panels does not stage operations.

## Status

`awaiting-user-confirmation`

## Reported symptoms

Files and folders cannot be dragged from one comparison panel to the other.

## Expected behavior

Dropping into the opposite panel stages copies for review. Folders include their
indexed contents. Staging changes neither the inventory databases nor disk files.

## Actual behavior

Drag initiation, acceptance, and drop handling require two live filesystem panels.

## Reproduction details

Load two inventory databases, compare folders, and drag a file or folder across.

## Affected area

Desktop drag handlers, staged plans, export, and execution review.

## Constraints

Preserve live-folder move behavior, content checks, link exclusions, and the
separate source and destination roots. Preserve unrelated workspace changes.

## Open questions

Native pointer delivery requires the user's desktop check after local regression
verification.
