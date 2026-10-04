# Fix attempt 001

## Attempt status

`awaiting-user-confirmation`

## Goal

Stage inventory copies through drag and drop, with reviewable, executable plans.

## Relation to previous attempts

First attempt for this bug.

## Proposed change

Enable inventory drag with a copy effect and bind its source panel in the payload.
Prepare recursive operations from snapshot metadata on a worker thread. Retain
source and target bindings for export and execution review; keep source databases
read-only. Enable F5 through the same path. Preserve live move staging.

## Risks

Root IDs may match across databases. Missing hashes, links, type collisions, and
case collisions must not yield unsafe copy plans. A staged plan has one direction;
changing direction requires clearing or exporting it first.

## Files and components

Inventory staging model, main view model, drag handlers, review/export bindings,
and focused desktop and plan regressions.

## Verification plan

Cover offline file and recursive folder copies, both directions, multi-selection,
deduplication, source locking, separate-root plan export, links, hashes, collision
handling, direct routed drops, and an isolated execution using temporary files.

## Implementation summary

Inventory drag payloads retain their originating panel, database, and root. Drops
on the opposite panel or a folder stage copies. F5 uses the same worker-thread
preparation. Folder contents are traversed without filesystem reads; source
hashes, scan status, exclusions, links, and target collisions are checked before
publishing any operations. Existing destination files produce VERIFY operations.

Staged plans retain separate source and target roots, even when root IDs match.
Review and export use those bindings. Source changes and direction changes are
blocked while operations are staged. JSON and database exports cannot overwrite
input inventories or companions. Exported databases release their SQLite handles
immediately. Live-panel move behavior is preserved.

## Test results

The first focused run passed 38 of 39 tests. Cleanup exposed a pooled handle on
the exported database; disabling pooling for plan output fixed that failure.
The subsequent combined run passed all 55 tests, including 17 new inventory drag
regressions. A routed GUI drop into a folder stages copies and enables review.
A temporary execution copies from the correct source with equal root IDs.
Offline snapshots and input database bytes remain unchanged by staging/export.
The Release GUI build passed with existing Avalonia constructor warnings.

## Outcome

Local verification passed. Native pointer delivery remains for user retesting.

## Next step

Commit this change separately and open the rebuilt GUI for the user.

## Remaining gaps

User confirmation of native drag and drop.
