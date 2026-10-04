# File differences

Report changed content at the same relative path, plus unchanged content at
different recorded paths, between inventory A and inventory B. A is the
before/reference snapshot; B is the after/compared snapshot.
Both inputs stay read-only and may be offline or historical copies of inventories
of the same directory. A successful complete scan is required on each selected
root. The report reads recorded hashes; it does not scan, hash, or move files.

## Desktop

1. Choose **Inventory → File differences...**. Loaded panel databases and
   selected roots provide the defaults. Select a different database or root on
   either side as needed.
1. Choose **Analyze differences**. Progress appears in the status line, and
   **Cancel** stops the work without publishing a partial result.
1. The default **Quick differences** filter shows content changes, moves, copies,
   removed copies, and ambiguous groups. **Content changed** isolates same-path
   differences, and **Location changes** keeps the move/copy view.
   Other filters expose unverified entries, content found
   only on one side, unchanged content, or all results. The summary always
   includes unverified counts.
1. Select a group to inspect each version's size and hash, or every recorded
   location and its retained, removed, or added state. Multi-location groups show
   counts instead of invented path pairs.
1. Choose **Export CSV...** or **Export JSON...** to export the current filter.
   **Swap A / B** reverses direction and clears results. **Refresh snapshots**
   reloads both inputs and clears results; analyze again afterward.

Loading, analysis, filtering, refresh, and export run in the background. Closing
the window cancels outstanding work. Root selection, direction changes, failed
analysis, and cancellation prevent exporting stale or partial results.

## Classifications

- Content changed: files at the same normalized relative path have different
  sizes or different current SHA-256 hashes. The row shows A/B sizes and hashes.
- Moved: exactly one recorded location in each inventory, at different relative
  paths. This includes renames.
- Copied: all original locations remain, with additional locations in B.
- Removed copies: some original copies disappear while at least one remains,
  without new locations.
- Ambiguous: duplicate content changes locations without a unique pairing,
  including a combination of removed and added copies.
- Only in A / Only in B: verified content has no counterpart on the other side.
- Unchanged: the content's relative paths match across both inventories.
- Unverified: a regular file lacks a fresh usable SHA-256 hash, or a known
  content group could have additional copies among same-size unverified files.

Same-path comparison checks successful scan metadata first. Different sizes
prove a content change even without hashes. Equal sizes need current full SHA-256
hashes on both sides; missing or stale hashes leave the pair Unverified. Scan
errors also leave a pair Unverified. Matching filenames across folders is deferred.
For example, `photos/album-a/01.jpg` and `photos/album-b/01.jpg` with different
content do not produce a Content changed row.

Location matching uses file size and full SHA-256. Shared paths are matched first,
but uniqueness counts every copy on each side. Paths ignore case only when both roots
are case-insensitive. Drive letters and physical root paths are context; equal
relative paths with the same content on different mounts are unchanged. Missing
files, links and their descendants, and paths excluded by either selected root
are omitted.

The labels describe snapshot differences. A file whose bytes changed is different
content, and these inventories do not establish which filesystem action occurred.

## CLI

`bn` means the published `BackupNormalizer` CLI or
`dotnet run --project src/BackupNormalizer --`.

```powershell
bn location-changes --source-db A.db --source-root photos --target-db B.db --target-root photos
bn location-changes --source-db A.db --source-root photos --target-db B.db --target-root photos --filter content-changed --json
bn location-changes --source-db A.db --source-root photos --target-db B.db --target-root photos --filter moved --json
bn location-changes --source-db A.db --source-root photos --target-db B.db --target-root photos --filter all-differences --format csv --output changes.csv
```

The default filter is `quick-differences`. Filters accept `quick-differences`,
`content-changed`, `changes`, `moved`, `copied`, `removed-copies`, `ambiguous`,
`only-in-a`, `only-in-b`, `unverified`, `unchanged`, `all-differences`, and `all`.
`changes` retains the location-only view. The desktop labels also work as quoted
filter values. `--json` or `--format json`
writes JSON to stdout when no output path is provided. `--format csv` writes CSV
to stdout. With `--output`, the default format is JSON; specify `--format csv` for
CSV. Errors return exit code 2.

JSON includes direction, database/root identity, root paths, scan dates, hash
readiness, full summary counts, the active filter, and the selected groups with
every location. Paired content rows include a `contentComparison` object with
`beforeSize`, `afterSize`, `beforeDigest`, and `afterDigest`. The common `digest`
is null for these rows; each location carries its actual metadata. The group-level
`size` retains A's size; use `contentComparison` for both versions. Unique moves
have explicit before/after paths. Summary counts refer to the full report, even
when groups are filtered.

CSV has one row per location, connected by a content-group ID, and repeats both
input identities and scan dates so direction remains clear. Size and digest
columns describe that row's side, including differing sizes or unavailable hashes.
Unique moves include before/after columns; ambiguous groups leave those columns empty. Commas, quotes,
and line breaks use CSV quoting. An empty result includes one `Report metadata`
row with blank location fields to preserve input identity and the selected filter.

Exports reject either input database and its SQLite companion paths. Files are
written through a temporary file; failed or cancelled exports preserve an
existing destination.

See the [feature specification](feature-spec.md),
[implementation plan](implementation-plan.md), and
[verification notes](verification.md).
