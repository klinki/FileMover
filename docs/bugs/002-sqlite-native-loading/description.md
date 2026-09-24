# SQLite native loading

## Status

- Awaiting user confirmation

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
