# File location changes implementation

The [feature specification](feature-spec.md) defines the report's behavior.

## Grouped filename and duplicate discovery extension

1. Extend the read-only report with opt-in extension-constrained filename
   families, content versions, per-inventory duplicate groups, and raw unknown
   locations. Build them from the complete eligible snapshots before one-sided
   row suppression. Preserve existing analysis API and classifications.
1. Add grouped views and metadata to shared JSON/CSV exports and CLI options.
   Keep filename candidates separate from proven same-path changes and expose
   duplicate counts only within the requested inventory.
1. Add desktop matching controls and expandable family/version/path trees, with
   duplicate side selection, selection details, active-view export, and background
   projection. Clear reports when analysis options change.
1. Verify repeated names, retained versions, unknown evidence, exclusions, case
   rules, complete copy counts, view lifecycle, exports, CLI validation, and a
   large duplicate family without a Cartesian expansion. Run Release checks.
1. Update usage and verification documentation. Leave changes uncommitted.

## Same-path content comparison extension

1. Index eligible regular files by normalized relative path while loading the
   existing read-only snapshots. Compare sizes and usable hashes on paired paths.
1. Add content-change and paired-unverified rows with both sizes and digests.
   Preserve complete content groups for move/copy decisions and avoid duplicate
   one-sided rows for paired paths.
1. Share a Quick differences default and Content changed filter across CLI,
   desktop, and exports. Display and export the actual metadata for each side.
1. Verify differing sizes, same-size differing hashes, missing/stale hashes, scan
   errors, case/path rules, repeated filenames across folders, and interaction
   with moves/copies. Update documentation and run proportionate checks.

## Original implementation

1. Add a core read-only comparison of two selected inventory roots. Match content
   by fresh SHA-256 and size, preserve complete location sets, and classify moves,
   copies, removed copies, ambiguous groups, one-sided content, and unverified
   entries. Report scan metadata, progress, and cancellation.
1. Share filtering and CSV/JSON export between the CLI and desktop app. Preserve
   direction and input metadata, and protect databases and SQLite companions.
1. Add the `location-changes` CLI command and an Inventory menu report window.
   Default selections to the loaded panels. Load, analyze, filter, refresh, and
   export on background workers; support cancellation and direction swapping.
1. Verify classification examples, incomplete and offline inventories, root
   isolation, case sensitivity, exclusions, stale hashes, export fidelity and
   guards, CLI behavior, and desktop lifecycle with focused tests. Run a Release
   build, applicable tests, and formatting checks.
1. Document the finished workflow and verification results. Preserve existing
   unrelated workspace changes; leave implementation changes uncommitted.
