# File content and location differences between inventories

## Status

Implemented in the core, CLI, and desktop app. See the
[usage guide](README.md) and [verification notes](verification.md).

## Goal

Compare a selected root from database A with a selected root from database B and
report unchanged file content recorded at different locations. Show the path in
A and the path in B, distinguishing moves, retained originals with new copies,
and ambiguous duplicate matches. Also report different file content at the same
root-relative path, with each side's size and current SHA-256 digest.

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
- Content differences between regular files at the same normalized relative path.
- Optional extension-constrained filename families and verified per-root duplicate
  discovery, retaining all recorded copies without arbitrary pairings.
- Offline inventories, without reading file contents or requiring mounted drives.
- Complete successful scans on both selected roots. Incomplete or unavailable
  scans block analysis with an explanation of which root needs a complete scan.

Synchronizing files, deleting duplicates, generating execution plans, scanning
or hashing automatically, and comparing all roots at once are outside this feature.

## Grouped filename comparison and duplicate discovery

The second mode is optional filename matching across folders, constrained by an
editable extension allowlist. Default the allowlist to `.zip,.mp4` and keep this
matching disabled until selected. Extensions ignore case and accept comma,
semicolon, or whitespace separators with optional leading dots. Reject empty
enabled lists, paths, wildcards, and malformed extensions. Match the complete
basename using the same case rules as relative paths.

Run the same-path analysis first. Filename families retain every eligible file
with that name in both selected roots, including repeated names and same-path
anchors. Show a family when both inventories contain the filename, their relative
location sets differ, and their verified content-version sets differ or evidence
is unverified. Families with identical version sets stay in the existing location
report. A family summarizes candidates, not proven replacement pairs.

- Group verified versions by size and current full SHA-256, retaining every path.
- Show versions shared across A/B, only in A, or only in B with copy counts.
- Keep each unverified file separate, with its size, path, and reason. Never merge
  unknown hashes into one content version or claim them as verified duplicates.
- Never generate all path pairs or all version pairs. Use indexes, version sets,
  and location lists. Runtime is linear in file count apart from sorting.
- Existing same-path and move/copy classifications keep their complete evidence.
  Filename results are a separate view and do not inflate their summary counts.

Duplicate discovery uses verified content identity across all filenames and
extensions, independently of filename matching. Count copies within A or within B,
never across the two snapshots. Preserve all locations even when a same-path
comparison hides redundant one-sided rows. For each content group with two or
more files on that side, show size, SHA-256, copy count, extra copies, and potential
savings `(copies - 1) × size` if one copy is retained. This is a recorded logical
byte estimate; no automatic deletion or keeper selection is part of this report.
Show unverified locations separately alongside the selected inventory's duplicates.

The desktop adds Filename differences and Duplicates views beside the existing
path/location results. Filename rows expand into content versions, then A/B path
lists. Duplicate rows expand into every path on the chosen side. Show counts and
matching basis in group labels and full metadata in selection details. Extension
or matching-mode edits clear stale analysis. Switching views uses background
projection with cancellation; export follows the active view and duplicate side.
The window has a minimum size of 800 × 720 to keep results visible with these controls.

CLI adds `--match-filenames`, `--extensions zip,mp4`, and filters
`filename-differences`, `duplicates-in-a`, and `duplicates-in-b`. Filename view
requires explicit filename matching. JSON exports grouped versions or duplicates
with analysis options and unknown locations. CSV uses one row per location with
family/version IDs, version status, copy counts, extra copies, potential savings,
and existing input metadata. Empty exports retain metadata.

## Same-path content differences

Compare files at the same normalized root-relative path before reporting content
locations. Ignore case only when both roots are case-insensitive.

- Different recorded sizes establish **Content changed** when both file entries
  have successful scan metadata, even if hashes are unavailable.
- Equal sizes and two usable current SHA-256 hashes establish **Content changed**
  when the digests differ. Matching hashes keep the existing location analysis.
- Equal sizes with missing or stale hashes produce a paired **Unverified** row.
  Scan-error entries also remain unverified even when their recorded sizes differ.
- Show both paths, sizes, and hashes. An unavailable hash stays explicitly unknown.
- Report a paired content difference once instead of two one-sided content rows.
  Keep all files in the content analysis so move/copy uniqueness remains correct.
  A content group shared across inventories can still report a move or copy in
  addition to a same-path replacement; these describe separate observed changes.
- Files with the same filename in different folders do not match in this mode;
  the optional grouped filename view handles those candidates separately.

The default **Quick differences** filter includes content changes and the existing
location-change classifications. Keep **Location changes** as a separate filter,
and provide **Content changed**, **Unverified**, and the existing other filters.
The CLI default is `quick-differences`; `--filter changes` keeps its existing
location-only meaning, and `--filter content-changed` isolates content differences.
CSV records each side's own size and digest on its location row. JSON adds a
`contentComparison` object containing both sizes and digests for paired rows.
Neither export invents a shared digest for different content.

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

- **Content changed:** files at the same normalized relative path have different
  successful recorded sizes or different current SHA-256 hashes. Show both
  versions together, including unavailable hashes.
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
- **Unverified:** equal-size files at a matching path lack hashes needed to
  compare their content, or an entry has scan errors. Elsewhere, a regular file
  lacks a usable current SHA-256 digest. Show its
  side, path, size, and reason. An unverified file of the same size as a content
  group could be another copy. Mark that group's location classification
  unverified and retain its known locations instead of claiming uniqueness,
  absence, or an unambiguous copy relationship.

## Examples

Location examples concern one content group with the same size and digest.

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
- A and B have `photo.jpg` with different digests: one Content changed row with
  both versions' sizes and hashes.
- A has `photos/album-a/01.jpg`; B has `photos/album-b/01.jpg` with different
  content: Only in A and Only in B. The filenames do not establish a match.
- A has one verified `old/photo.jpg`; B has one matching `new/photo.jpg` and a
  same-size file with a stale hash: Unverified, because uniqueness is unknown.

## Desktop workflow

Add **Inventory → File differences...**. Default A and B to the loaded
database panels when available. Allow selecting each database and root, and
swapping the report direction. Display database names, selected roots, recorded
root paths, scan dates, and hash readiness.

Run analysis in the background with progress and cancellation. Keep the desktop
responsive during analysis and filtering. The default view shows Content changed,
Moved, Copied, Removed copies, and Ambiguous results, with an explicit unverified count and
access to those entries. Filters expose each classification, all differences,
and unchanged content.

Show classification, path in A, path in B, sizes in A/B, and location counts per side.
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
- Same-path comparisons produce one paired row with each side's metadata.
  Different sizes establish a change without hashes; equal sizes require usable
  hashes on both sides. Scan errors prevent a definitive comparison.
- Filename families retain duplicates and every location without arbitrary
  source/destination pairs. Extension filtering affects only this candidate view.
- Duplicate counts and savings apply to one selected root at a time, across
  filenames. Unverified entries never establish duplicate identity.
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
