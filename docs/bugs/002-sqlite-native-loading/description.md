# SQLite native loading

## Status

- Investigating QNAP runtime startup failure

## Reported symptoms

Database-backed tests fail on Windows with `DllNotFoundException: sqlite3`. The current package setup and provider-selection code are more complex than needed for ordinary desktop use.

## Expected behavior

The CLI, UI, and tests should open SQLite databases using the standard `Microsoft.Data.Sqlite` NuGet package. The QNAP image should use the same package and be checked on its ARMv7 host.

## Reproduction details

Run `dotnet test BackupNormalizer.slnx` on Windows. Before this attempt, 19 tests failed and an isolated database test showed the missing native `sqlite3` library.

## Affected area

The [core package references](../../../src/BackupNormalizer.Core/BackupNormalizer.Core.csproj), [database startup](../../../src/BackupNormalizer.Core/Database.cs), [CLI startup](../../../src/BackupNormalizer/Program.cs), and [QNAP container](../../../Dockerfile.qnap).

## Constraints

Keep existing dark-theme edits intact. Do not claim QNAP compatibility until the acceptance script runs on the actual NAS.

## On-device update, 2026-10-05

The actual ARMv7 QNAP reports 32,768-byte pages and kernel 4.2.8. The old container image, a fresh current CLI publish, and an independent minimal .NET 10.0.10 probe crash with SIGSEGV. The probe does not reach its first managed output and has no EF Core or SQLite references. GDB identifies the crash in `libcoreclr.so`; this does not establish the cause.

The existing Windows native-library repair remains separately verified. The QNAP deployment is not compatible in its tested state. No existing NAS share was mounted in these tests.
