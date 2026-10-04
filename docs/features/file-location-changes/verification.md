# File differences verification

Verified on 2026-10-04 against the [feature specification](feature-spec.md).

## Results

- Release projects compiled successfully as part of the test runs.
- Full solution tests passed: 417 passed, 20 skipped, 0 failed. The skips are
  existing native NTFS/symlink and headless input tests.
- All 48 focused cases passed, including 17 added for same-path comparisons.
  The eight desktop cases passed again after simplifying the details display.
- CSharpier checked all seven C# files changed by this extension. Whitespace checks and
  feature documentation links passed.

```powershell
dotnet test BackupNormalizer.slnx -c Release --no-restore -p:UsedAvaloniaProducts= --filter 'FullyQualifiedName~FileLocationChangesTests|FullyQualifiedName~UiLocationChangesTests' --results-directory TestResults/same-path-content --logger 'trx;LogFileName=focused.trx'
dotnet test BackupNormalizer.slnx -c Release --no-restore -p:UsedAvaloniaProducts= --results-directory TestResults/same-path-content --logger 'trx;LogFileName=full.trx'
dotnet test BackupNormalizer.slnx -c Release --no-restore -p:UsedAvaloniaProducts= --filter 'FullyQualifiedName~UiLocationChangesTests' --results-directory TestResults/same-path-content --logger 'trx;LogFileName=ui-final.trx'
```

The empty `UsedAvaloniaProducts` property disables Avalonia build telemetry for
these sandboxed checks. The standard telemetry target attempted to write its log
outside the workspace. Existing Avalonia constructor and xUnit analyzer warnings
remain unrelated to this feature.

## Coverage

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
window at 800 × 620 verifies populated
root selections, positive result-grid bounds, and enabled exports after analysis.
A 20,000-group report verifies analysis and row projection on worker threads,
filtering, responsive command state, and disabled exports while busy.

The shared classifier is in
[FileLocationChanges.cs](../../../src/BackupNormalizer.Core/FileLocationChanges.cs),
and exports are in
[LocationChangesExport.cs](../../../src/BackupNormalizer.Core/LocationChangesExport.cs).
The desktop behavior is in
[LocationChangesViewModel.cs](../../../src/BackupNormalizer.Ui/ViewModels/LocationChangesViewModel.cs)
and [LocationChangesWindow.axaml](../../../src/BackupNormalizer.Ui/Views/LocationChangesWindow.axaml).

## Limits

Verification used synthetic inventories and the headless desktop test host. No
live drive inventory was rescanned or modified, and native file pickers were not
operated manually. The existing command-line and storage-picker workflows are
reused. The report classifies recorded content locations; it does not establish
filesystem action history or pair changed content by filename across folders.
