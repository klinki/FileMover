# Bug status

## Current state

- Ubuntu 22.04 works on QNAP, but .NET 10 CoreCLR and .NET 6 startup checks fail. Native AOT runtime, SQLite, and precompiled EF query samples work. Inventory query conversion and migration startup remain open.

## Active attempt

[Fix attempt 005](fix-attempt-005.md)

## Last updated

2026-10-05

## Confirmation date

Awaiting user confirmation.

## Resolution summary

EF Core's SQLite provider passes Windows tests and publishes an ARM32 musl native library. On-device QNAP checks now fail during .NET runtime startup. A minimal probe without EF Core or SQLite also crashes before managed entry.

Native AOT testing subsequently passed on the same NAS, including hashing, temporary file operations, and an independent SQLite check. The full CLI's version/help commands work under AOT, but database and scan checks stop at EF model construction. The full application remains unverified and the bug remains open.

Ordinary Ubuntu 22.04 and `linux-arm` were subsequently tested. Ubuntu works, while CoreCLR 10.0.10 fails before managed entry with a misleading out-of-memory error. A system-call trace records an `mprotect` request at an address aligned to 16 KiB rather than the kernel's 32 KiB pages. Docker did not report an OOM kill, and available RAM was approximately 5.5 GiB. Changing the C library does not resolve the runtime startup failure.

The independent .NET 6.0.36 hello-world builds also exited 139 before managed output under Ubuntu 22.04 (`linux-arm`) and Alpine 3.17 (`linux-musl-arm`). Disabling WriteXorExecute did not help. The Ubuntu trace fails before loading CoreCLR, so its precise cause cannot be equated with the .NET 10 failure. Binary hashes matched and original services remained running.

## Attempt history

- [Fix attempt 001](fix-attempt-001.md): implemented and locally verified; QNAP confirmation pending.
- [Fix attempt 002](fix-attempt-002.md): user requested an on-device Native AOT experiment after the CoreCLR startup failure.
- [Fix attempt 003](fix-attempt-003.md): user authorized testing ordinary Ubuntu 22.04 with the glibc-based `linux-arm` runtime.
- [Fix attempt 004](fix-attempt-004.md): user requested a trivial .NET 6 hello-world test on the actual QNAP.
- [Fix attempt 005](fix-attempt-005.md): user approved an inventory-first AOT plan and requested the first EF model/query compatibility trial.

## State change log

- 2026-09-24: Bug opened from the Windows native-library failure and package review.
- 2026-09-24: Fix attempt 001 started.
- 2026-09-24: Regular package and QNAP image configuration applied.
- 2026-09-24: Full Windows suite passed, CLI database smoke test passed, and ARM32 musl publish completed.
- 2026-09-24: Awaiting the QNAP acceptance test on the actual NAS.
- 2026-09-24: EF Core data access replaced direct SQL; an EF-generated initial migration creates new database schemas.
- 2026-10-05: Connected to the actual NAS through the existing SSH identity and QNAP's Docker executable. Confirmed ARMv7, 32,768-byte pages, and kernel 4.2.8.
- 2026-10-05: Existing image and fresh self-contained .NET 10.0.10 application both failed on the NAS. A minimal independent .NET probe exited 139. The NAS acceptance check remains failed; no repair has been claimed.
- 2026-10-05: GDB located the crash in `libcoreclr.so`. Disabling executable-memory dual mapping or ReadyToRun did not allow the probe to start. Root cause remains under investigation.
- 2026-10-05: Native AOT probe experiment started with the user's authorization. Linux build prerequisites are being prepared inside the workspace.
- 2026-10-05: Native AOT runtime and SQLite probes passed on the actual 32 KiB-page NAS. Full CLI publication succeeded; version/help passed, while database/scan commands reported that EF requires a compiled model. Recorded feasibility results; production application source was not changed.
- 2026-10-05: Ubuntu 22.04 compatibility experiment started. The user supplied a repository reporting this base image working on a TS-431P3. Runtime and application support will be checked independently.
- 2026-10-05: Ubuntu 22.04 base and native dependencies passed on the NAS. CoreCLR 10.0.10 `linux-arm` probe and CLI failed before managed entry. ReadyToRun and executable-memory settings did not resolve the failure; strace captured a misaligned `mprotect` returning `EINVAL`. File transfers and all seven original running services were verified. No production repair was applied; bug remains open.
- 2026-10-05: Started the authorized .NET 6 compatibility experiment using an independent hello-world program. Application source and target frameworks remain unchanged.
- 2026-10-05: .NET 6.0.36 self-contained hello-world builds failed under both Ubuntu 22.04 and Alpine 3.17 with SIGSEGV before managed output. Default and WriteXorExecute-disabled runs failed; Ubuntu tracing shows an earlier native startup failure than .NET 10. All transferred binary checks and original-service checks passed. No application repair was applied; bug remains open.
- 2026-10-05: Started the authorized EF Core AOT trial. Current-facade and candidate-static-query Windows baselines passed 14 checks each. Full query precompilation rejects current dynamic queries; a bounded static-query experiment is being built. Production application source remains unchanged.
- 2026-10-05: EF Core AOT static-query trial passed all 14 checks on the actual NAS. Compiled model discovery, reads, writes, hash persistence, bulk updates/deletion, and transaction commit/rollback work. The unconverted facade opens its database but fails query execution without precompilation. Explicit managed Linux generation and tested query-shape adjustments are documented. Retain EF for further inventory query conversion; full application compatibility remains open.
