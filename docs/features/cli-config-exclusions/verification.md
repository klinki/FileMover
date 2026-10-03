# Verification

Verified on Windows on 2026-10-03 in Release configuration.

- Focused config, exclusions, migrations, inventory status, and USN regressions:
  63 passed, one existing permission-dependent test skipped.
- Full suite: 299 passed, 20 existing tests skipped, zero failures. This includes
  29 new cases for config defaults, precedence, validation, exclusion scope,
  reintroduced files, migration compatibility, and planning preservation.
- Deterministic enumeration fixtures verify excluded metadata failures and
  reparse entries, without requiring elevated filesystem access.
- A plan execution fixture verifies that files protected by either root's regex
  policy remain untouched and cannot supply move or trash candidates.

```powershell
dotnet test tests/BackupNormalizer.Tests/BackupNormalizer.Tests.csproj -c Release --no-restore --verbosity minimal -p:UsedAvaloniaProducts=
```

Existing skipped tests cover privileged Windows symlink/MFT/USN checks and
unsupported headless pointer interactions. The existing Debug
`WithDeveloperTools` reference issue remains outside this change.

See the [usage guide](README.md) and [implementation plan](implementation-plan.md).

The implicit-config follow-up passed all 22 CLI config tests in Release. Eight
new subprocess cases verify current-directory settings discovery, explicit
selection before and after the command, environment selection, legacy fallback,
missing-file defaults, and default config creation. The subprocess fixtures
isolate their working directories from other tests. Release binaries were
rebuilt by the test run.
