# Container verification

## State

Prepared and locally verified on 2026-10-01. Actual NAS execution is pending. The user confirmed ARM32v7; the image retains the repository's TS-431P3 and 32 KiB target.

## Artifact

- Image tag: `backup-normalizer:qnap-arm32`
- Image platform: `linux/arm/v7`
- Docker image config ID: `sha256:35a076a74bdd7fba36c1e4b0a5025ab2daeec876ab459dc885d9aa4d48203385`
- Deployment folder: `publish/nas/`, regenerated with the [build helper](../../../deploy/build-qnap.ps1)
- Archive: `backup-normalizer-qnap-arm32.tar`, 43,248,640 bytes
- Archive SHA-256: `02F70D5801570797B1FFF4F13FB9499DE545E5DCD5062E054849B1E919BAAF0F`
- Configuration: `compose.yaml` and `.env.example`

## Completed checks

- Built and exported the self-contained CLI on an AMD64 SDK stage for ARMv7 Alpine. Imported the archive into local Docker and confirmed its architecture and variant.
- Inspected the apphost, `libcoreclr.so`, and bundled `libe_sqlite3.so`. All are ARM32 ELF binaries with 65,536-byte load-segment alignment and offsets congruent at 32 KiB boundaries. This is static evidence, not an on-device compatibility result.
- Under ARM emulation, `--version`, `db-test`, and `scan-test` passed. A read-only generated-data mount scanned two files, including a Unicode filename, and hashed both. A second hash run reused both cached hashes. Python independently verified both stored SHA-256 digests against the fixture bytes.
- Six [job orchestration checks](../../../tests/qnap-inventory-test.sh) passed using a mock CLI: successful command order, database failure, registration failure, scan failure, hash failure, and a missing data directory. Failures stopped the job before subsequent inventory steps and appeared in its log.
- Compose configuration resolved the ARMv7 platform, read-only data mount, persistent state mount, one hash worker, terminal allocation, and disabled restart. Missing required NAS paths failed configuration validation.
- Shell syntax checks passed for both runtime scripts.
- A container with an unspecified terminal width reproduced one-character hashing progress. The follow-up in [hash-progress attempt 002](../../bugs/009-hash-progress/fix-attempt-002.md) restored the full progress line. Seven focused .NET hashing tests passed.
- `git diff --check` passed.

## Build details

The default Docker driver rejected archive export on this installation. The build helper now creates or reuses its own `docker-container` builder, leaving the current default builder and Docker Desktop settings unchanged. The rebuild with this driver succeeded.

The initial build context contained approximately 223 KiB. The allowlist excludes inventory databases, generated binaries, tests, UI sources, and private workspace folders.

## Pending on the NAS

1. Import the archive into Container Station.
1. Run against a small existing NAS directory and a separate persistent state directory.
1. Confirm `armv7l`, a page size of `32768`, SQLite checks, a complete scan, successful hashes, and readable progress.
1. After the container exits, copy the inventory and any WAL companion together and verify the inventory on the PC.

Local emulation reported a page size of `4096`. It cannot verify the NAS's kernel or page size. QNAP support and the hash-progress bug remain open until the corresponding user checks are confirmed.

Changes are not committed. No NAS files have been accessed or modified, and no image has been pushed to a registry.

## Actual NAS test, 2026-10-05

The user authorized Docker testing on the NAS. Portainer was reachable and the existing SSH identity allowed access to QNAP's Docker executable. The host reports `armv7l`, 32,768-byte pages, kernel 4.2.8, and Docker 26.1.4-qnap2.

The existing October 1 image crashed on .NET startup. A fresh current CLI build and an independent .NET 10.0.10 probe were copied into a separate test image. The fresh image's database and scanner checks also crashed. The probe exited 139 before managed entry. GDB located SIGSEGV inside CoreCLR, without symbols to identify the function.

Compatibility is failed on this host in the tested configuration. The earlier emulator and static alignment checks were insufficient to establish support. The [QNAP runtime investigation](../../bugs/002-sqlite-native-loading/initial-findings.md) records the evidence.

Only isolated test containers, images, and synthetic Docker volumes were created. No existing NAS share was mounted or modified, no existing service was stopped or edited, and no image was pushed.

## Native AOT test, 2026-10-05

Native AOT runs on the actual ARMv7 QNAP with 32,768-byte pages. A minimal probe passed managed startup, SHA256, and temporary file read/write/move checks. A separate SQLite probe passed open/create/insert/read/transaction rollback using Microsoft.Data.Sqlite 10.0.12 and bundled SQLite 3.53.3.

The full CLI also published to Native AOT and passed `--version` and `--help` on the NAS. Its database and scan checks failed with `Model building is not supported when publishing with NativeAOT. Use a compiled model.` This experimental image is not ready for inventory or move replay. EF model/query/migration handling and JSON serialization still need AOT compatibility work.

Retained test images on the NAS:

| Image | Tested entrypoint | Result |
| --- | --- | --- |
| `backup-normalizer:qnap-aot-probe-20261005` | `/app/AotProbe` | Runtime, hashing, file operations passed |
| `backup-normalizer:qnap-aot-sqlite-probe-20261005` | `/app/SqliteProbe` | SQLite passed |
| `backup-normalizer:qnap-aot-app-experiment-20261005` | `/app/BackupNormalizer` | Version/help passed; database/scan blocked |

These images derive from the existing Alpine test image and retain its original default entrypoint. Select the entrypoint shown above when inspecting them in Portainer; do not use the full CLI experiment for a production inventory job. Test runs used a read-only filesystem, writable `/tmp`, and no existing NAS share mounts.

The [Native AOT investigation](../../bugs/002-sqlite-native-loading/fix-attempt-002.md) links the local source snapshots, build diagnostics, and on-device JSON results. Full application support remains open; the Docker build/deployment helpers have not been converted to AOT.

## Ordinary Ubuntu test, 2026-10-05

The user authorized an ordinary Ubuntu 22.04 test with the glibc-based `linux-arm` target and supplied [qnap-32k-containers](https://github.com/laroy-sh/qnap-32k-containers) as compatibility evidence. Ubuntu 22.04.5, glibc 2.35, its shell, and the required native runtime dependencies all worked on the actual ARMv7 NAS with 32,768-byte pages.

The independent CoreCLR 10.0.10 probe and the current CLI failed before managed entry, reporting `Failed to load System.Private.CoreLib.dll` and `Out Of Memory`. The NAS had approximately 5.5 GiB available, and Docker recorded no OOM kill. A system-call trace captured `mprotect(0x733ac000, 16384, PROT_READ|PROT_WRITE)` failing with `EINVAL`; that address is not aligned to the actual 32 KiB page size. ReadyToRun and executable-memory setting changes did not resolve startup. SQLite, scan, and hash workflows could not be tested in this configuration.

Measured uncompressed image sizes were 56.6 MB for Ubuntu alone, 95.8 MB with native .NET dependencies, and 180.2 MB for the complete experimental image. The retained image is `backup-normalizer:ubuntu-app-test-20261005`, with `/app/BackupNormalizer` as its default entrypoint. It is not usable for production inventory jobs in this tested configuration.

The [Ubuntu investigation](../../bugs/002-sqlite-native-loading/fix-attempt-003.md) records build versions, image digest, diagnostic variants, and local evidence. All seven original services remained running, no existing NAS share was mounted, and transferred binaries matched their local SHA256 hashes. Application source and deployment helpers were not changed; no image was pushed and no commit was created.

## .NET 6 hello-world test, 2026-10-05

An independent `net6.0` hello-world program was published with SDK 6.0.428 and runtime 6.0.36 for both Ubuntu 22.04 (`linux-arm`) and Alpine 3.17 (`linux-musl-arm`). Both builds exited 139 before printing anything on the actual ARMv7 QNAP with 32,768-byte pages. Disabling WriteXorExecute gave the same result. Docker reported no OOM kills, transferred binary hashes matched, and all seven original services remained running.

The Ubuntu trace records an early native SIGSEGV before captured CoreCLR loading. The exact cause remains unconfirmed. These official .NET 6 distributions are therefore not a working fallback in the tested configurations. The application was not retargeted, and database/scan/hash workflows were not tested with .NET 6.

The retained images are `backup-normalizer:net6-hello-test-20261005` and `backup-normalizer:net6-musl-hello-test-20261005`, both with `/app/HelloNet6` as their default entrypoint. The [.NET 6 experiment](../../bugs/002-sqlite-native-loading/fix-attempt-004.md) records the probe and evidence. Test containers had no existing NAS share mounts. No production application changes, commit, or push were made.

## EF Core AOT trial, 2026-10-05

The first EF AOT trial passed all 14 candidate static-query checks on the actual NAS using the production database model/entities, EF Core 10.0.12, and Native AOT runtime 10.0.10. It confirmed reads, writes, hash persistence, bulk updates/deletion, transaction commit/rollback, and a legacy read-only query. The executable reported ARM, 32,768-byte pages, and dynamic code disabled.

The retained image is `backup-normalizer:ef-aot-trial-20261005-171638`, with the synthetic static suite as its default command. The unchanged facade is also tested separately; model-backed opening works, while its queries fail without precompilation. This diagnostic image does not perform inventory collection or production upgrades.

[AOT verification](../native-aot-cli/verification.md) records generation adjustments, source, and evidence. ELF alignment and transfer hashes passed, all seven original services remained running, and no existing NAS share was mounted. Inventory conversion and migration startup were pending at this first-trial milestone.

## Actual AOT inventory application, 2026-10-05

The actual CLI and separate migration helper subsequently passed the synthetic on-device inventory job. The working image is `backup-normalizer:inventory-aot-20261005-182026`. Creation, root updates, scanning, exclusions, links, hashing/reuse, changed-file rescanning, status, export, and a backed-up legacy upgrade passed. The portable export opened through the Windows CLI. Transfer hashes and all original services were verified; no existing NAS share was mounted.

The [application evidence](../native-aot-cli/verification.md#application-verification) includes the original NAS result and logs. The clean runtime image `backup-normalizer:inventory-aot-runtime-20261005-183056` subsequently built through the NAS Docker engine and passed the same inventory acceptance job. It contains the AOT application, migration helper, SQLite library, and job scripts, and does not contain the earlier CoreCLR files. Docker reports 208,600,488 bytes uncompressed. [Clean image evidence](../native-aot-cli/verification.md#clean-runtime-image-verification) records the build and second acceptance result. Comparison and replay are not yet qualified for AOT.

## PC-prepared plan execution, 2026-10-05

The clean runtime image `backup-normalizer:inventory-aot-runtime-20261005-185747` subsequently passed both inventory and execution jobs on the actual NAS. Seven PC-prepared plans and sixteen operations verified moves, copies, safe trash, links, conflicts, root binding, and recovery. Independent database checks validated operation states, timestamps, errors, and seventeen journal rows; the exported execution database opened on Windows. Database creation and backed-up legacy upgrades also passed. Docker reports ARM and 210,436,728 bytes uncompressed, with no CoreCLR files.

The [execution evidence](../native-aot-cli/verification.md#nas-execution-qualification) preserves results and logs. No existing NAS shares were mounted, no test container was OOM-killed, and all original services remained running. Comparison and plan creation/import remain PC workflows.
