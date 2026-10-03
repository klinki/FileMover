# GUI workflow verification

Verified on Windows on 2026-10-03 with .NET 10 in Release configuration.

The full suite passed 270 tests, skipped 20, and failed none. This includes 34
new tests for the five GUI improvements. The skipped tests already existed:
10 require Windows symlink permissions, two require native USN access, and eight
cover headless pointer interactions that the existing test host cannot simulate.

```powershell
dotnet test tests/BackupNormalizer.Tests/BackupNormalizer.Tests.csproj -c Release --no-restore --verbosity minimal
```

Focused checks covered:

- Inventory health, scan diagnostics, legacy read-only loading, and hash readiness.
- Directional database planning, isolated writable snapshots, conflicts, skipped
  links, JSON export, and preservation of inventory database contents.
- Inventory search, comparison filters, entry details, and usable file lists at
  both 1280 by 800 and the minimum 860 by 560 window size.
- Scan and hash cancellation, incomplete scans, cache reuse, progress, snapshot
  refresh, and cancellation before the main window closes.
- Portable SQLite export, refusal to replace existing files, independent panel
  restoration, missing roots and folders, invalid preferences, and saved layout.

The first full run exposed a shortcut test that focused the newly added hidden
plan field. The test now chooses a visible enabled text box and asserts that
focus succeeds. Its regression check and the subsequent full run passed.

Release builds remain the supported verification route for this delivery.
The pre-existing Debug `WithDeveloperTools` reference issue was outside this
change. Native file pickers and privileged Windows link and USN scenarios still
need interactive verification in the user's environment.

See the [approved implementation plan](implementation-plan.md) and the
[GUI usage guide](../../../src/BackupNormalizer.Ui/README.md).
