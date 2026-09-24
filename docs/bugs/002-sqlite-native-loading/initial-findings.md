# Initial findings

## Confirmed facts

- The [core project](../../../src/BackupNormalizer.Core/BackupNormalizer.Core.csproj) references `Microsoft.Data.Sqlite.Core` and `SQLitePCLRaw.bundle_sqlite3`; that bundle loads a system SQLite library.
- The original `SqliteInit` code probed the provider and fell back to the same system library. This file was removed in the fix.
- The [QNAP image](../../../Dockerfile.qnap) installs Alpine `sqlite-libs` and sets `BN_SQLITE=system`.
- The previous Windows test run had 19 failures. An isolated failure was `DllNotFoundException: sqlite3`.

## Likely cause

The project selects the system SQLite bundle on every platform. This Windows host does not provide a compatible native `sqlite3` library at the expected name.

## Unknowns

- Whether the regular package's native SQLite build is included in a `linux-musl-arm` publish.
- Whether that native build loads on the QNAP's 32 KB OS page-size environment.

## Reproduction status

The missing-library failure was reproduced before this attempt. Re-run database tests after changing the package and inspect the QNAP publish output.

## Investigation update, 2026-09-24

The regular package restored successfully. The full Windows test suite and CLI database smoke test pass. A self-contained `linux-musl-arm` publish includes `libe_sqlite3.so`, resolving the first unknown above. Its ARM32 ELF load segments report 64 KB alignment and are congruent at 32 KB boundaries. This inspection does not replace execution on the NAS.
