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
