# Record links without failing scans

## Summary

Retain file symlinks, directory symlinks, and junctions in the database with their target paths. Skip them during content processing. Broken links and unavailable link metadata remain non-fatal.

Artifact: `implementation-plan.md`  
Slug: `symlink-inventory`  
Save path: `C:\ai-workspace\FileMover\docs\features\symlink-inventory\implementation-plan.md`

External-target copying and link recreation are deferred.

## Implementation

- Separate entry kind from scan status. Add `EntryKind` with values `File`, `FileLink`, `DirectoryLink`, and `ReparsePoint`, plus nullable `LinkTarget`, `TargetPath`, and `LinkNote` fields. Junctions use `DirectoryLink`; unidentified reparse points remain distinguishable.
- Preserve target text exactly. Obtain the absolute immediate target path with `ResolveLinkTarget(false)`. Do not resolve the full chain, inventory target contents through links, or hash those contents. This API also supports junctions and nonexistent targets. [Microsoft documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.resolvelinktarget?view=net-10.0).
- Detect links before reading file length. Store size zero and best-effort link timestamps, using the scan timestamp and null creation time when unavailable. Record metadata problems in `LinkNote`, leaving status `Ok` and `Error` empty. Link entries increment the scanned count without incrementing errors; CLI wording becomes “entries.”
- Recursive enumeration yields directory links without descending into them. MFT enumeration also emits directory reparse entries and excludes descendants beneath them. Genuine directory-enumeration failures retain existing incomplete-scan behavior.
- Add an EF migration and update database projections and writes. Existing entries marked `UnsupportedEntry` with `"symlink"` become non-fatal `ReparsePoint` entries with a rescan note. Preserve historical scan statuses; only a successful rescan establishes completeness. Read-only databases lacking the new columns use legacy projections without modification.
- Hashing, matching, deduplication, and survivor checks accept only regular files. Invalidate cached hashes whenever an entry changes kind, including conversion back to a regular file. Recheck filesystem paths before hashing to catch links introduced after scanning.
- Manual staging omits links and linked subtrees. Automatic planning preserves paths excluded by source links and skips operations whose destination is a link or lies beneath one.
- Add `SKIP_LINK` operations with an optional `SkipReason` to stored operations and exported plan JSON. Record excluded links and blocked paths explicitly; expose their count in plan summaries. Import accepts older plans without the new field.
- Execution records these operations as `Skipped` and continues. It also checks source, destination, and relevant parent components below the effective roots before ordinary operations. Newly discovered links produce an explained skip. Links cannot serve as verified survivors for trash operations; existing survivor requirements remain enforced.
- Inventory panels display link kind, target, and notes. Directory links cannot be navigated. Add a `Skipped` comparison state: links contribute neither content differences nor scan errors, and regular-entry versus link collisions show a type conflict. Exclude skipped links from the differences-only view and report them separately.

## Verification

- Cover relative and absolute targets, targets inside and outside the root, broken links, chains, cycles, directory links, junctions, and unavailable metadata. Assert zero link-related scan errors and no traversal of linked directories.
- Verify recursive/MFT parity, link disappearance, retargeting, regular-file/link transitions, hash invalidation, and preservation of unseen entries after genuine scan failures.
- Test migrations, legacy read-only inventory loading, skipped comparison states, target display, and disabled directory-link navigation.
- Test staging, planning, JSON round-trips, destination collisions, linked parents, and links introduced after scanning. Verify unaffected files still process and linked contents remain untouched.
- Run focused regressions and the full suite in Release. The existing baseline passed 21 focused tests; Debug currently fails on the unrelated `WithDeveloperTools` reference. Run real Windows symlink and MFT checks where permissions allow, supplementing them with deterministic fixtures.

## Delivery defaults

Save the approved plan before implementation, preserve unrelated changes, and make no commits or pushes.

Document the repair under `docs/bugs/010-symlink-scan-errors/`. After verification, leave its status awaiting user confirmation. The [bug-fixing skill](C:/Users/david/.agents/skills/bug-fixing/SKILL.md) requires: “Until the user explicitly confirms the fix, keep the bug open and record the state as `awaiting-user-confirmation`.”
