# USN incremental scans delivery

Implemented on 2026-10-02 according to the [implementation plan](implementation-plan.md).

## Delivered behavior

- `scan` defaults to `--usn auto`. A successful full scan on an accessible local NTFS journal establishes a checkpoint. Subsequent scans refresh affected entries without enumerating the whole filesystem or loading all inventory rows.
- `--full` requests full enumeration while retaining journal-based invalidation of known changed content. `--usn off` removes the checkpoint and uses ordinary full scanning. The persisted setting is `usnMode`.
- Checkpoints include root and volume identities, the journal ID, retained position, and successful scan ID. Writable inventories migrate automatically; historical read-only inventories remain readable without modification.
- File creation, deletion, content changes, and ordinary file renames refresh affected paths. Unchanged entries retain their metadata, last observation IDs, and usable hashes.
- Known content and identity changes invalidate every cached hash algorithm, including same-size writes with restored timestamps. Hashing remains a separate command.
- Incremental updates and checkpoint advancement commit together. A failed refresh rolls back inventory changes and requires a full scan next time. Journal changes detected during a refresh trigger one full-scan retry.
- The full-scan checkpoint is captured before enumeration. Still-open changes observed during journal replay keep the checkpoint at their earliest record until closure.
- Directory namespace, access, or link changes, hard-link changes, case-only renames, ambiguous casing, unavailable parent identities, unknown record versions or reasons, journal gaps, and windows above one million records cause full scanning. Hard-linked changes are checked across the volume because their recorded name can be outside the selected root.
- A discarded change window invalidates cached hashes conservatively. This can make `hash --needed` read files that otherwise appear unchanged.
- Link traversal protections, non-fatal link notes, database exclusion, and preservation of unseen entries after genuine scan errors remain enforced. Database exclusion now uses filtered database queries rather than materializing the inventory.
- Terminal progress and final summaries identify entries refreshed using USN. Terminal progress also shows fallback reasons. Redirected and progress-disabled scans retain summary-only stdout.

## Verification

- Full Release suite: 224 passed, 19 skipped, zero failures, 243 total.
- Deterministic coverage includes parsing bounds and versions, Unicode names, complete file identities, checkpoint migration and compatibility, creates, deletes, renames, same-size writes, hash invalidation, unchanged cache reuse, journal gaps and resets, root and volume replacement, incomplete and interrupted scans, rollback, changes during baseline enumeration, still-open handles, database exclusion, and directory and hard-link fallbacks.
- The real Windows junction fixture passed. A linked parent forces full enumeration, linked descendants become missing in the inventory, and external contents remain untouched and unhashed.
- Fresh Release rebuild: zero errors and four existing warnings, consisting of one Avalonia warning and three xUnit analyzer warnings.
- Built CLI smoke checks passed for `--usn off`, `--full`, automatic fallback without journal access, `hash --needed` cache reuse, rejection of an invalid journal mode, and startup.

The 19 environmental skips comprise eight headless UI tests, nine file-symlink tests, one elevated MFT test, and one elevated USN test. This session lacks administrator access, so the real journal reader still needs validation on an elevated Windows NTFS volume. No native speedup is claimed from the deterministic fixtures.

## Files and build

- [Windows reader and replay](../../../src/BackupNormalizer.Core/Usn.cs)
- [Scanner integration](../../../src/BackupNormalizer.Core/Scanner.cs)
- [Checkpoint migration](../../../src/BackupNormalizer.Core/Migrations/20261002173131_TrackUsnCheckpoints.cs)
- [Inventory tests](../../../tests/BackupNormalizer.Tests/UsnScanTests.cs), [parser tests](../../../tests/BackupNormalizer.Tests/UsnParserTests.cs), and [real journal test](../../../tests/BackupNormalizer.Tests/UsnWindowsTests.cs)
- [Release CLI folder](../../../src/BackupNormalizer/bin/Release/net10.0/)

## Usage

Run scans with administrator access to enable journal use when the volume already has a journal:

```powershell
.\BackupNormalizer.exe --elevate scan d --db .\d.db
.\BackupNormalizer.exe hash --needed --db .\d.db
```

Existing inventories need one successful full scan to establish their first checkpoint. Merely upgrading or manually changing an old scan status does not establish a USN baseline. ReFS, network shares, other filesystem journal backends, and automatic journal creation are outside this implementation.

The user's live inventories were not modified. Unrelated working-tree changes were preserved.
