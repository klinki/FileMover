# Native AOT inventory on QNAP

The first AOT application build collects inventories using the actual CLI and shared EF Core database code. It targets the ARMv7 QNAP with 32,768-byte pages. The [verification record](verification.md) distinguishes the first EF trial from application acceptance.

Supported commands are `init`, `config`, `root`, `scan`, `hash`, `status`, `db export`, `db-test`, `scan-test`, help, and version. Comparison, planning, and replay remain a later qualification milestone and the AOT CLI rejects those commands before database access. The Windows build retains its complete command set.

## Build the application

The build requires Python, WSL Ubuntu, and the tested Linux SDK 10.0.302, Clang/LLD 18.1.3, Alpine 3.17 ARM sysroot, and compiler wrapper from the NAS investigation. These prerequisites currently live under the local ignored `publish/nas-test-20261005` directory. A clean-machine toolchain installer is not provided yet.

Run from the repository root, choosing a fresh output directory:

```powershell
python deploy/prepare-inventory-aot.py publish/inventory-aot-local
wsl -d Ubuntu -- sh /mnt/c/ai-workspace/FileMover/deploy/build-inventory-aot.sh /mnt/c/ai-workspace/FileMover/publish/inventory-aot-local /mnt/c/ai-workspace/FileMover/publish/nas-test-20261005
```

[Snapshot preparation](../../../deploy/prepare-inventory-aot.py) copies maintained sources without changing the application. The [build script](../../../deploy/build-inventory-aot.sh) generates migration SQL using the ordinary build, generates the EF model and query interceptors from managed Linux assemblies, and publishes the CLI and migration helper for `linux-musl-arm` with 65,536-byte ELF load alignment. The output `app` directory contains both native executables and `libe_sqlite3.so`. Keep those three files together.

EF tooling and packages are pinned to 10.0.12. The [interceptor corrections](../../../deploy/fix-ef-interceptors.py) add a type alias for nested `Database` row types and correct reversed parameter extraction for chained setters. The first application NAS run exposed the setter-order error through a corrupted synthetic root path. The build correction is specific to this generator version and must be reviewed when upgrading EF. Interceptors must be regenerated after source edits.

## Database startup and upgrades

The AOT CLI checks migration history before creating its EF context. Current databases open directly. New databases and recognized older schemas invoke `BackupNormalizer.Migrations` beside the CLI. The helper contains forward SQL generated from the existing EF migrations. A SHA256 comparison of the embedded package prevents mismatched application/helper upgrades.

Read-only opens never invoke the helper, and `root list` opens read-only in both builds. Unknown, newer, or incomplete histories fail writable startup. The helper serializes cooperating upgrade attempts, obtains SQLite write locks and rechecks history, commits each migration together with its history row, and verifies integrity and foreign keys. Existing inventories receive a consistent SQLite backup before upgrade. Backups and the persistent `.bn-migration.lock` file remain beside the database.

The generator rejects migrations containing transaction-suppressed SQL. Future migrations involving such operations need explicit support before publication. Failed transactional steps roll back and later startup can retry from the committed history.

## Test on the NAS

The [acceptance runner](../../../deploy/test-inventory-aot-nas.py) uses an existing SSH identity and the previously imported Alpine test image. Supply a synthetic legacy fixture from the [EF trial](../../../tools/BackupNormalizer.AotTrial/README.md):

```powershell
python deploy/test-inventory-aot-nas.py publish/inventory-aot-local --host nas --legacy-fixture publish/ef-aot-trial-manual-20261005/fixtures/legacy.db
```

The [acceptance job](../../../deploy/inventory-aot-acceptance.sh) creates temporary files and databases inside a new container. It tests creation, root updates, exclusions, links, hashing, cache reuse, file changes/moves/removal, diagnostics, export, and a backed-up legacy upgrade. Python independently checks the exported database's schema, file metadata, and SHA256 digests. The runner verifies binary alignment, transferred hashes, and original services. It mounts no existing NAS shares and preserves the image, logs, and stopped containers.

## Package an inventory job

Start Docker Desktop with Linux containers, then run:

```powershell
.\deploy\build-qnap.ps1
```

To package an already published build without regenerating it:

```powershell
.\deploy\build-qnap.ps1 -SkipNativeBuild -NativeBuildDirectory .\publish\inventory-aot-local
```

The helper prepares a small Docker context containing only the two executables, SQLite library, and job scripts. [Deployment instructions](../nas-container/README.md) cover importing the archive, choosing a read-only data mount, and keeping persistent inventory state separately. Select test paths before running against NAS shares.

Alternatively, build directly through the NAS's Docker engine using the existing SSH identity:

```powershell
python deploy/build-inventory-image-nas.py publish/inventory-aot-local --host nas
```

This streams the same Docker context to the local daemon socket over SSH, avoiding Docker CLI staging in the NAS's small `/tmp`. It records a unique image tag and build log beside the native output. The verified clean image already on the NAS is `backup-normalizer:inventory-aot-runtime-20261005-183056`; its default command displays inventory help. No existing shares have been scanned with it yet.
