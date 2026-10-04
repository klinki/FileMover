# Desktop inventory creation and JSON configuration

## Goal

Create and scan a local inventory from the desktop app without first using the
CLI. Read the existing JSON configuration format and apply its scan and hashing
settings to desktop inventory jobs.

## Implementation

1. Reuse the shared config loader, including default discovery, validation,
   and legacy fallback. Add a file picker for choosing a config. Resolve relative
   database paths from the working directory, matching the CLI.
1. Add an Inventory menu action to choose a folder, root ID, and new database.
   Offer scanning alone or scanning followed by hashing. Fill defaults from the
   active folder and loaded configuration.
1. Extend the existing background job runner to create a database and register
   its root. Refuse existing database files and sidecars. Validate the folder and
   config before creating files, and reserve the destination without overwriting.
1. Use configured scan modes, exclusions, hash algorithm, and worker count for
   both new and existing inventory jobs. Reuse progress and cancellation, then
   display the new inventory in the panel that initiated the scan.
1. Verify creation, hashing, exclusions, failure without writes, cancellation,
   config validation, panel refresh, and desktop bindings. Document the workflow,
   rebuild, and open the final GUI for review.

## Acceptance

- A first scan can start while both panels show live folders.
- Loading configuration and opening the setup window do not create a database.
- Creation preserves existing inventories and never changes the selected files.
- New scans show progress and support cooperative cancellation.
- Completed and incomplete new inventories are available for browsing.
- Config errors leave the previous desktop settings active.
- Existing inventory jobs continue to use their selected database and root.
