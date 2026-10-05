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

## On-device investigation, 2026-10-05

- Portainer was reachable through the user's existing browser session. The direct Docker endpoint uses TLS. The existing SSH identity worked, but the Docker CLI was absent from the remote PATH; QNAP's executable under `/share/CACHEDEV1_DATA/.qpkg/container-station/bin/docker` worked without changing host configuration.
- The actual host reports ARMv7, an Annapurna Labs AL314 Cortex-A15 CPU, 32,768-byte pages, Linux 4.2.8, and Docker 26.1.4-qnap2.
- Imported the existing October 1 image and published the current CLI from HEAD `1816b89` with SDK 10.0.302 and its cached .NET 10.0.10 ARM32 musl runtime. The current apphost, CoreCLR, and SQLite libraries have 65,536-byte load alignment and offsets congruent at 32 KiB boundaries.
- The existing image failed at runtime startup. The current CLI failed both database and scanner checks with segmentation faults.
- A minimal probe using only the .NET runtime also exited 139 before printing its first managed line. This rules out EF Core and SQLite as necessary triggers of this startup failure.
- GDB reported SIGSEGV inside `libcoreclr.so`. Native debug symbols were not available, so the exact failing function remains unknown.
- Disabling `DOTNET_EnableWriteXorExecute` or `DOTNET_ReadyToRun` did not make the probe start.
- Test containers had no mounts of existing NAS shares. Synthetic data lived in a new Docker volume and was mounted read-only during compatibility checks. Existing services were not stopped or edited.
- Native AOT's current documented target list includes Linux ARM. That does not establish QNAP compatibility. The app's EF Core queries and migration-based database startup require separate AOT compatibility work.

The [captured on-device results](../../../publish/nas-test-20261005/on-device-results.json), [host trace](../../../publish/nas-test-20261005/host-trace.log), and [minimal probe source](../../../publish/nas-test-20261005/probe-source/Program.cs) are local artifacts. No Native AOT binary has been produced or tested yet.

## Native AOT follow-up, 2026-10-05

- Built `linux-musl-arm` Native AOT probes with SDK 10.0.302, runtime 10.0.10, Clang/LLD 18.1.3, and an Alpine 3.17 ARMv7 musl sysroot. Set ELF load alignment to 65,536 bytes; verified the probe and full CLI are congruent at 32 KiB page boundaries.
- The runtime probe reached managed entry on the NAS, reported a page size of 32,768 and disabled dynamic code, and passed SHA256 plus temporary file read/write/move checks.
- The full unmodified CLI source snapshot published to Native AOT with warnings. `--version` and `--help` exited 0. `db-test` and `scan-test /app` exited 2 with an EF error requiring a compiled model.
- A separate probe using Microsoft.Data.Sqlite 10.0.12 loaded the bundled SQLite library and passed create/insert/read/rollback checks on the NAS. SQLite reported version 3.53.3. Native SQLite loading is therefore viable in this tested AOT configuration.
- JSON and EF warnings identify further compatibility work; those paths were not all exercised. The regular CoreCLR crash remains unexplained, and the full AOT application is not ready for inventory or replay use.
- Test containers used only their own writable temporary storage. Existing NAS shares were not mounted; all seven original services remained running. No production application source was changed.

[Attempt 002](fix-attempt-002.md) records the commands, results, and local artifact links. Native AOT feasibility is established for the tested runtime and SQLite operations, without claiming full application compatibility.

## Ubuntu follow-up, 2026-10-05

The user supplied [qnap-32k-containers](https://github.com/laroy-sh/qnap-32k-containers) and authorized an ordinary Ubuntu 22.04 test. Its ARMv7 userland works on the actual NAS. The base image reports Ubuntu 22.04.5 LTS and glibc 2.35, and native .NET dependencies installed successfully.

The independent CoreCLR 10.0.10 `linux-arm` probe and current CLI both failed before managed entry with a CoreLib loading error reported as out of memory. Docker recorded no OOM kill, and approximately 5.5 GiB of RAM was available. Disabling ReadyToRun did not help; disabling executable-memory dual mapping caused SIGSEGV.

A strace capture records CoreLib file mapping succeeding, followed by `mprotect(0x733ac000, 16384, PROT_READ|PROT_WRITE)` returning `EINVAL`. The starting address is aligned to 16 KiB, not the actual 32 KiB page size. This is evidence of a dynamic runtime page-alignment problem despite compatible ELF load segments. The exact CoreCLR source location and the cause of the earlier musl crash remain unidentified.

[Attempt 003](fix-attempt-003.md) records the Ubuntu experiment and evidence. SQLite, scan, and hash checks were not run because managed startup failed. All original services remained running; no existing NAS shares were mounted. Full application compatibility remains open.

## .NET 6 follow-up, 2026-10-05

The user requested a hello-world test because articles suggested .NET 6 might work on this QNAP. An independent program published with the installed SDK 6.0.428 and official runtime 6.0.36 failed before managed output in both Ubuntu 22.04 (`linux-arm`) and Alpine 3.17 (`linux-musl-arm`). All default and WriteXorExecute-disabled runs exited 139, without Docker OOM kills.

The Ubuntu strace capture ends in `SIGSEGV` with `SEGV_ACCERR` before any captured CoreCLR loading. Its cause remains unconfirmed and cannot be assumed to be the same failing `mprotect` seen with .NET 10. The .NET 6 apphosts and CoreCLR libraries use 4 KiB ELF segment alignment; their offsets are congruent modulo 32 KiB, which alone does not establish working page permissions.

Five binary hashes per distribution matched after transfer, and all seven original services remained running. No NAS share was mounted. [Attempt 004](fix-attempt-004.md) records the source, exact versions, result matrix, and trace. Application source and target frameworks remain unchanged; this experiment establishes failure only for the two tested official distributions.

## EF Core AOT follow-up, 2026-10-05

[Attempt 005](fix-attempt-005.md) confirms that EF Core 10.0.12 can execute statically precompiled query samples with the production context/model on this NAS under Native AOT 10.0.10. All 14 candidate checks passed, including root/file reads and writes, hash persistence, bulk updates/deletion, and transaction commit/rollback.

The full current query set cannot precompile because it includes dynamic composition and shared expressions. The bounded build's unchanged facade opens the database with the generated model but fails its 13 query-backed checks without interceptors. This isolates the remaining work to application query conversion and schema startup, rather than proving that every existing operation is supported.

Successful generation used managed Linux assemblies without an ARM runtime identifier, followed by ARM publication. The candidate queries required local context variables and the expression setter overload for numeric updates. All binary/fixture hashes matched, all seven original services remained running, and no existing NAS share was mounted. Production source remains unchanged.
