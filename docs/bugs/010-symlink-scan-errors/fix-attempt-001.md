# Fix attempt 001

## Attempt status

awaiting-user-confirmation

## Goal

Record links without failing scans and skip link-related content operations.

## Relation to previous attempts

First attempt.

## Proposed change

Implement the approved [symlink inventory plan](../../features/symlink-inventory/implementation-plan.md), including metadata migration, non-fatal scanning, explicit skips, execution checks, and inventory display.

## Risks

Filesystem paths can change between inspection and use. Older read-only databases lack the new columns. MFT enumeration must not expose linked descendants.

## Files and components

Core data model, scanner, MFT, paths, hashing, matching, planner, staging, executor, CLI, inventory UI, and regression tests.

## Verification plan

Use actual links where supported and deterministic fixtures for metadata errors and MFT ancestry. Run focused tests followed by the full Release suite. Preserve the existing Debug build issue outside this repair.

## Implementation summary

Added entry-kind and link metadata columns with an EF migration and legacy read-only projections. The scanner records links before length access, and both enumeration modes retain directory links without traversing linked descendants. Hashing, matching, staging, planning, execution, and survivor checks exclude links. Explicit SKIP_LINK operations retain reasons through JSON and database import. Inventory panels show target metadata, block linked-folder navigation, and exclude links from content differences and scan errors.

## Test results

Baseline: 21 focused Release tests passed before implementation.

- The first focused run found a SQLite connection-pooling issue in the new read-only fixture; disabling fixture pooling resolved it.
- Real junction assertions passed, but recursive .NET fixture cleanup required mount-point permissions. Tests now unlink only their own junction directory entries through RemoveDirectoryW before ordinary cleanup.
- Final full suite: 147 passed, 17 skipped, zero failures, out of 164 tests. The 19 new cases include ten passing deterministic and real-junction tests, eight file-symlink tests skipped because creation is unavailable, and one elevated MFT test skipped.
- Real junction checks covered target metadata, missing targets, cycles, recursive/synthetic-MFT parity, hashing, staging, execution, and rejection of linked survivors.
- Existing tests cover incomplete scans preserving unseen inventory. New tests cover schema upgrade, unchanged legacy read-only databases, kind transitions, hash invalidation, plan round-trips, old JSON, protected target counterparts, skipped comparisons, and navigation.
- Command: `dotnet test tests/BackupNormalizer.Tests --configuration Release --no-restore --verbosity minimal -p:UsedAvaloniaProducts=`. The last property disables build telemetry that otherwise attempts writes outside the workspace.
- The unrelated Debug WithDeveloperTools compilation issue remains outside this repair. Existing Avalonia and xUnit warnings remain unchanged.

## Outcome

Implemented and locally verified. User confirmation is pending.

## Next step

Rescan a user root containing links and confirm that the scan completes with no link-related errors. Historical incomplete scans remain unchanged until rescanned.

## Remaining gaps

File-symlink integration tests and the elevated real-volume MFT comparison remain unverified in this environment. The path checks do not provide atomic protection against concurrent filesystem replacements between inspection and use. User confirmation is pending.
