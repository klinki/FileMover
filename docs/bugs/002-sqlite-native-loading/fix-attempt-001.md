# Fix attempt 001

## Attempt status

Awaiting QNAP confirmation.

## Goal

Use regular `Microsoft.Data.Sqlite` on all targets and verify the Windows database path and QNAP publish.

## Relation to previous attempts

First attempt for this bug.

## Proposed change

- Replace the core project's split SQLite package references with `Microsoft.Data.Sqlite`.
- Remove manual provider initialization from the database and CLI.
- Remove the QNAP system SQLite override and update setup documentation.
- Run database tests and inspect the `linux-musl-arm` publish output.

## Risks

The bundled native library may lack a compatible QNAP ARMv7 build or fail to load on the NAS. Only the on-device acceptance script can settle that.

## Files and components

The [core package references](../../../src/BackupNormalizer.Core/BackupNormalizer.Core.csproj), [database](../../../src/BackupNormalizer.Core/Database.cs), [CLI](../../../src/BackupNormalizer/Program.cs), [QNAP Dockerfile](../../../Dockerfile.qnap), [README](../../../README.md), and [specification](../../../backup-normalizer-spec.md).

## Verification plan

Restore, build, run the full test suite, publish for `linux-musl-arm`, and confirm the native SQLite file is present in the publish output.

## Implementation summary

Replaced the split SQLite references with `Microsoft.Data.Sqlite`, removed `SqliteInit` and its startup calls, and removed the Alpine `sqlite-libs` package and `BN_SQLITE` override from the QNAP image. The image now includes the QNAP acceptance script, with LF line endings enforced for Alpine. Updated the README and specification to describe the bundled native library and required NAS check.

## Test results

NuGet restore succeeded. The full Windows suite passed with 59 tests passed and 8 skipped. The CLI `db-test` passed from both a Debug run and the Release executable. Release build passed. The self-contained `linux-musl-arm` publish passed and contains the ARM32 musl `libe_sqlite3.so`. The native library's ELF load segments have 64 KB alignment. Docker daemon access was denied in the sandbox, and the application has not run on the QNAP.

## Outcome

Windows SQLite loading is locally verified. QNAP support remains open until the acceptance script runs on the device.

## Remaining gaps

Run the [QNAP acceptance script](../../../qnap-check.sh) inside the rebuilt image on the actual NAS, where `getconf PAGESIZE` reports 32768.
