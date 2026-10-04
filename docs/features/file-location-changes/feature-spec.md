# File location changes between inventories

## Status

Implemented in the core, CLI, and desktop app. See the
[usage guide](README.md) and [verification notes](verification.md).

## Goal

Compare a selected root from database A with a selected root from database B and
report unchanged file content recorded at different locations. Show the path in
A and the path in B, distinguishing moves, retained originals with new copies,
and ambiguous duplicate matches.

A is the before or reference inventory; B is the after or compared inventory.
These roles belong to this report and do not change the databases. Results
describe recorded snapshots, using "Moved" and "Copied" as classifications of
the observed locations rather than a log of filesystem actions.

## Scope

- A read-only report in the desktop app and CLI, with CSV and JSON export.
- One selected inventory root on each side. The databases can differ or be the
  same with different roots. Identical recorded physical root paths are allowed,
  including historical snapshots of the same directory.
- Regular files matched by size and usable full SHA-256 digest, including renamed
  files and files in different folders.
- Offline inventories, without reading file contents or requiring mounted drives.
- Complete successful scans on both selected roots. Incomplete or unavailable
  scans block analysis with an explanation of which root needs a complete scan.

Synchronizing files, generating execution plans, tracking files whose content
changed, scanning or hashing automatically, and comparing all roots at once are
outside this feature.

## Matching rules

1. Load the selected inventories read-only. Exclude missing entries, links and
   their descendants. Apply both roots' stored path exclusions to both sides,
   consistent with the existing inventory diff.
1. Group eligible files by size and current full SHA-256 digest. Filenames and
   timestamps alone never establish a content match.
1. Match shared root-relative paths within each content group first. Ignore case
   only when both roots record case-insensitive filesystems. Normalize recorded
   path separators without depending on the current operating system.
1. Retain every location in each group, including the shared paths. Evaluate
   uniqueness against the whole group, before removing unchanged locations.
1. Compare the remaining locations using the classifications below. Sort results
   and location lists consistently so repeated analysis produces the same report.

Physical mount paths are context, not the comparison key. Identical relative
paths under different drive letters do not count as a move. A same-path file
with a different digest is different content and cannot anchor a copy match.

## Classifications

- **Unchanged:** all recorded locations for a content group match. Hide these
  groups by default.
- **Moved:** the content appears exactly once in A and exactly once in B, at
  different relative paths. Emit one explicit A path to B path pair. Renames
  follow the same rule.
- **Copied:** at least one original location remains in B, new locations appear
  in B, and no original locations disappear. Show the retained locations and
  each new copy. If several originals remain, do not invent which one supplied
  the bytes.
- **Removed copies:** some original locations disappear, at least one remains,
  and no new locations appear. Show the removed and retained locations without
  labeling this as a move.
- **Ambiguous:** content exists on both sides and locations change, but the
  preceding rules do not establish a move or copy. Show all A and B locations,
  including retained, removed, and added paths. Never pair duplicate locations
  arbitrarily. A mixture of added and removed duplicate paths is ambiguous even
  when one original remains.
- **Only in A / Only in B:** verified content has no match on the other side.
  These are supporting differences, available through a filter, rather than
  claimed location changes.
- **Unverified:** a regular file lacks a usable current SHA-256 digest. Show its
  side, path, size, and reason. An unverified file of the same size as a content
  group could be another copy. Mark that group's location classification
  unverified and retain its known locations instead of claiming uniqueness,
  absence, or an unambiguous copy relationship.

## Examples

Each example concerns one content group with the same size and digest.

- A has `old/photo.jpg`; B has `new/photo.jpg`: Moved.
- A has `photo.jpg`; B has `photo.jpg` and `backup/photo.jpg`: Copied.
- A has `one/photo.jpg` and `two/photo.jpg`; B has both originals and
  `three/photo.jpg`: Copied, with both original locations listed.
- A has `old/photo.jpg` and `kept/photo.jpg`; B has `new/photo.jpg` and
  `kept/photo.jpg`: Ambiguous. Removing the shared path does not make the
  remaining paths a proven unique move.
- A has `one/photo.jpg` and `two/photo.jpg`; B has `three/photo.jpg` and
  `four/photo.jpg`: Ambiguous, with every location listed.
- A has `photo.jpg` and `backup/photo.jpg`; B has `photo.jpg`: Removed copies.
- A and B have `photo.jpg` with different digests: separate content differences,
  not a move or a copy.
- A has one verified `old/photo.jpg`; B has one matching `new/photo.jpg` and a
  same-size file with a stale hash: Unverified, because uniqueness is unknown.

## Desktop workflow

Add **Inventory → File location changes...**. Default A and B to the loaded
database panels when available. Allow selecting each database and root, and
swapping the report direction. Display database names, selected roots, recorded
root paths, scan dates, and hash readiness.

Run analysis in the background with progress and cancellation. Keep the desktop
responsive during analysis and filtering. The default view shows Moved, Copied,
Removed copies, and Ambiguous results, with an explicit unverified count and
access to those entries. Filters expose each classification, all differences,
and unchanged content.

Show classification, path in A, path in B, size, and location counts per side.
For groups with several locations, show counts in the list and all locations in
the details view, including their retained, removed, or added state. Do not
display a single invented source/destination pair for ambiguous groups.

Provide **Export CSV** and **Export JSON**, applying the current filter. Record
the report direction and input metadata in exports. Refresh explicitly reloads
both snapshots and clears previous results. Export becomes available only after
analysis succeeds; cancelling or failing analysis cannot export partial results.

## CLI and exports

Expose a dedicated `location-changes` command with `--source-db`, `--source-root`,
`--target-db`, and `--target-root`, mapping source to A and target to B. Support
`--filter`, `--json`, and `--output` with `--format csv|json`. Default console
output uses the desktop's default classifications and always reports unverified
counts. Selecting JSON without an output path writes the report to stdout.

JSON contains report direction, input database/root metadata, scan dates, summary
counts, classification, size, digest when known, all locations in A and B, shared
paths, removed paths, added paths, and any verification reason. A unique move has
an explicit before/after pair. Other groups retain location arrays.

CSV contains one row per location, with content-group ID, classification, side,
database, root ID, relative path, location state, size, digest, and verification
reason. Unique moves also repeat their before/after paths on each row. Group IDs
connect duplicate locations without inventing pairings. Use normal CSV quoting
for commas, quotes, and line breaks in filenames.

Export must reject destinations matching either input database or a SQLite
companion file. Analysis and export never modify the input inventories or files.

## Acceptance criteria

- The examples above produce the specified classifications and complete paths.
- Shared paths are matched before changes are classified, while uniqueness uses
  all original locations. Duplicate groups never receive arbitrary move pairs.
- Missing or stale hashes cannot establish identity or a definitive location
  classification for potentially affected same-size groups.
- Incomplete scans block analysis; offline complete inventories remain usable.
- Selecting roots isolates their entries, even when databases share root IDs.
- Reports accept historical snapshots with overlapping recorded physical roots.
- Case sensitivity, exclusions, links, missing entries, and cross-platform paths
  follow the matching rules. Same-path changed content is not classified as moved.
- Swapping A and B reverses unique move pairs and recomputes copy/removal labels.
- CSV and JSON preserve every relevant location, input identity, and verification
  reason, including ambiguous groups and names requiring CSV quoting.
- Analysis, cancellation, filtering, refresh, and export keep the UI responsive
  and preserve input databases, including their schema and SQLite companions.

## Existing behavior

Reuse the content identity and snapshot conventions described in the
[backup coverage guide](../backup-coverage/README.md). Keep the path-based
[database comparison](../gui-database-comparison/README.md) and the operational
[database planner](../gui-database-planning/README.md) as separate workflows.
