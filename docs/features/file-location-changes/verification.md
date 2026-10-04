# File differences verification

Verified on 2026-10-04 against the [feature specification](feature-spec.md).

## Results

- Release solution compilation completed successfully as part of the test runs.
- Full solution tests passed: 464 passed, 20 skipped, 0 failed. The skips are
  existing native NTFS/symlink and headless input tests.
- All 95 feature cases passed in the full run, including 24 added for grouped
  filename/duplicate reports and 23 for report path exclusions. All 12 desktop
  cases also passed in a focused run.
- CSharpier checked all eight C# files changed by the path exclusion extension. Whitespace checks and
  feature documentation links passed.

```powershell
dotnet test BackupNormalizer.slnx -c Release --no-restore -p:UsedAvaloniaProducts= -p:OutputPath=bin/PathExclusions/Release/ --filter 'FullyQualifiedName~UiLocationChangesTests' --results-directory TestResults/path-exclusions --logger 'trx;LogFileName=ui.trx'
dotnet test BackupNormalizer.slnx -c Release --no-restore -p:UsedAvaloniaProducts= -p:OutputPath=bin/PathExclusions/Release/ --results-directory TestResults/path-exclusions --logger 'trx;LogFileName=full.trx'
```

The empty `UsedAvaloniaProducts` property disables Avalonia build telemetry for
these sandboxed checks. The standard telemetry target attempted to write its log
outside the workspace. Existing Avalonia constructor and xUnit analyzer warnings
remain unrelated to this feature.

The first attempt to build into the standard Release directory was blocked by
running app instances holding its DLLs open. The successful runs used the separate
output directory shown above and left those processes running.

## Coverage

[Report exclusion tests](../../../tests/BackupNormalizer.Tests/FileDifferenceExclusionsTests.cs)
verify literal file and subtree exclusions before every comparison mode, sibling
prefix boundaries, separator normalization, deduplication, case rules, invalid
paths, and existing stored policies. Excluded unknown files cannot undermine
move uniqueness, and excluded copies do not inflate duplicate counts or savings.
Checks preserve both input databases and retain exclusion metadata in JSON/CSV,
including empty exports and names requiring quoting. CLI checks cover repeatable
options and missing values. Desktop checks verify stale-report invalidation,
blank-line handling, reanalysis, invalid input, and visible results at minimum
window size with the exclusions section expanded.

[Core and CLI tests](../../../tests/BackupNormalizer.Tests/FileLocationChangesTests.cs)
verify unique moves, copies with one or several retained originals, removed
copies, ambiguous duplicate locations, unchanged content, one-sided content,
direction reversal, root isolation, equal recorded physical roots, path separator
normalization, and case sensitivity.

Same-path checks cover equal-size different hashes, different sizes with or
without hashes, paired uncertainty for stale/invalid/missing hashes, scan errors,
case rules, separator normalization, and root isolation. Repeated filenames in
different folders stay separate. Replacement alongside moved content preserves
every copy for uniqueness, and unpaired one-sided copies remain visible. Swapping
direction reverses both sizes and digests. JSON and CSV retain each side's own
metadata, while the CLI default includes content changes and `changes` keeps its
location-only behavior.

[Grouped report tests](../../../tests/BackupNormalizer.Tests/GroupedFileReportsTests.cs)
verify repeated names, several content versions, complete paths, shared versus
one-sided versions, opt-in extension filtering, normalization and validation,
case rules, same-path/copy-only family suppression, unknown hashes, stale hashes,
scan errors, exclusions, missing entries, and links. They verify read-only database
preservation and separate A/B duplicate counts across filenames, including
locations suppressed in the flat report. A 4,000-file family produces two versions
with 4,000 total locations, with no Cartesian path expansion. Grouped JSON/CSV and
CLI checks cover options, counts, savings, quoting, unknown locations, empty-view
metadata, and explicit filename-mode validation.

They also cover incomplete or unavailable scans, missing/stale/invalid hashes,
scan errors, conservative same-size uncertainty, missing entries, links and
linked descendants, both inventories' stored exclusions, and preservation of
input database bytes. Export checks cover CSV quoting, every ambiguous location,
JSON input metadata and direction, filtering, destination protection for both
databases and SQLite companions, cancellation, and temporary-file cleanup.

[Desktop tests](../../../tests/BackupNormalizer.Tests/UiLocationChangesTests.cs)
verify root selection, report invalidation, filtering, filtered export, swapping,
refresh, failed loading, incomplete analysis, cancellation, window disposal, and
source changes during analysis. Same-path rows display both sizes and known or
unavailable hashes; content and location filters remain separate. A headless
window at the new minimum of 800 × 720 verifies populated
root selections, positive result-grid bounds, and enabled exports after analysis.
A 20,000-group report verifies analysis and row projection on worker threads,
filtering, responsive command state, and disabled exports while busy. The same
test checks 1,000 duplicate groups with two child paths each and verifies grouped
projection runs on workers. New window checks exercise expandable filename trees,
tab switching, duplicate side selection, details, active-view exports, and stale
report invalidation after option changes.

The shared classifier is in
[FileLocationChanges.cs](../../../src/BackupNormalizer.Core/FileLocationChanges.cs),
and exports are in
[LocationChangesExport.cs](../../../src/BackupNormalizer.Core/LocationChangesExport.cs).
Grouping and grouped exports are in
[GroupedFileReports.cs](../../../src/BackupNormalizer.Core/GroupedFileReports.cs)
and [GroupedFileReportsExport.cs](../../../src/BackupNormalizer.Core/GroupedFileReportsExport.cs).
The desktop behavior is in
[LocationChangesViewModel.cs](../../../src/BackupNormalizer.Ui/ViewModels/LocationChangesViewModel.cs)
and [LocationChangesWindow.axaml](../../../src/BackupNormalizer.Ui/Views/LocationChangesWindow.axaml).

## Limits

Verification used synthetic inventories and the headless desktop test host. No
live drive inventory was rescanned or modified, and native file pickers were not
operated manually. The existing command-line and storage-picker workflows are
reused. The report classifies recorded content locations; it does not establish
filesystem action history or choose replacement pairs within filename families.
Duplicate savings are estimates from recorded logical sizes. No deletion or
keeper selection was implemented.
