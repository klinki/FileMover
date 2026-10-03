# Fix attempt 001

## Attempt status

`awaiting-user-confirmation`

## Goal

Identify the drive and database in the missing-source-hash error and verify
the documented rescan workflow for applying exclusions to an existing DB.

## Relation to previous attempts

First attempt for this diagnostic.

## Proposed change

Add the source root ID, absolute root path, and database path to the existing
exception. Keep the relative file path and hashing requirement. Document a
rescan example for an existing database.

## Risks

Only diagnostic text changes. Exclusion behavior remains unchanged.

## Files and components

- [Planner](../../../src/BackupNormalizer.Core/Planner.cs)
- [Planner tests](../../../tests/BackupNormalizer.Tests/PlannerTests.cs)
- [Configuration guide](../../features/cli-config-exclusions/README.md)

## Verification plan

Run Release planner, exclusion, and CLI configuration regressions. Verify
context when both databases use the same root ID. Verify that a successful
rescan excludes an already indexed unhashed file, allows unaffected files to
be planned, and preserves an excluded target counterpart.

## Implementation summary

The missing-hash exception now includes the selected source root ID, absolute
root path, and source database path alongside the relative file path.
The configuration guide now gives a rescan workflow for existing databases.
Two regressions verify ambiguous root IDs across databases and rescan-based
exclusion of an already indexed unhashed generated file.

## Test results

On 2026-10-03, 48 focused Release tests passed with no skips or failures:

```powershell
dotnet test tests/BackupNormalizer.Tests/BackupNormalizer.Tests.csproj -c Release --no-restore `
  --filter "FullyQualifiedName~PlannerTests|FullyQualifiedName~PathExclusionTests|FullyQualifiedName~CliConfigTests" `
  --verbosity minimal -p:UsedAvaloniaProducts=
```

The initial run passed 47 tests and failed one new assertion that incorrectly
expected zero skipped hashes. The excluded file correctly counts as skipped;
correcting the assertion to one skip made the regression pass.

The exclusion regression verifies successful planning and execution of an
included file while the excluded files on both disks remain unchanged.
The build refreshed the Release CLI. Existing xUnit analyzer and Avalonia
loader warnings remain unrelated to this change.

## Outcome

Locally verified. Awaiting explicit confirmation from the user's environment.

## Next step

Try the rebuilt CLI and confirm the missing-hash error identifies the correct
root and database.

## Remaining gaps

Confirmation from the user's environment is required before closing the bug.
