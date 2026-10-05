# Run an inventory on the QNAP NAS

The image targets the repository's QNAP TS-431P3 environment, ARMv7 with a 32 KiB kernel page size. The job uses the NAS's Docker engine through Container Station. It scans and hashes a mounted NAS directory and persists its SQLite inventory in a separate share. The desktop planner stays on the PC.

Docker containers share the host kernel. The actual NAS must pass the bundled compatibility check before this deployment is considered supported. The existing Alpine 3.17 baseline is preserved for that check. QNAP documents the affected models and 32 KiB page-size requirement in its [container compatibility guide](https://www.qnap.com/en/how-to/tutorial/article/how-to-debug-and-fix-common-container-issues-in-container-station).

## Build on the PC

The QNAP image now packages the Native AOT inventory CLI and its matching migration helper. Follow the [AOT build instructions](../native-aot-cli/README.md) to prepare the tested WSL SDK and ARM toolchain, then start Docker Desktop with Linux containers and run:

```powershell
.\deploy\build-qnap.ps1
```

For an existing AOT publication:

```powershell
.\deploy\build-qnap.ps1 -SkipNativeBuild -NativeBuildDirectory .\publish\inventory-aot-local
```

The helper generates EF migration SQL and precompiled queries on the PC, publishes the CLI and helper through WSL, and prepares a Docker context containing only the native runtime files and job scripts. Buildx exports the image archive, Compose file, and example environment file. The runtime image contains no SDK or CoreCLR. Neither .NET nor SQLite needs installation on the NAS host.

Buildx's [Docker exporter](https://docs.docker.com/build/exporters/oci-docker/) creates a single-platform image archive. The build does not push to a registry.

## Import into Container Station

1. Ensure Container Station is installed on the NAS.
1. Create a small test directory with a few representative files, for example `/share/BackupNormalizerTest`. Create a separate persistent state directory, for example `/share/BackupNormalizerState`.
1. In Container Station, import the generated image archive. Confirm the imported tag is `backup-normalizer:qnap-arm32-aot`.
1. Create a Compose application using the generated configuration. Replace both `${NAS_DATA_PATH:?...}` and `${NAS_STATE_PATH:?...}` values with the actual absolute NAS directories in the YAML. Container Station's editor need not support a separate environment file.
1. Start the application and inspect its logs. It runs compatibility checks, registers the root, scans it, and hashes its files. A successful job exits with code `0`; it does not restart automatically.

QNAP's [Container Station quick start guide](https://www.qnap.com/en/how-to/tutorial/article/container-station-quick-start-guide) covers importing local images and managing applications.

The `/data` mount is read-only. The `/state` mount holds the database and its SQLite companions. The container exposes no port and requires no privileged mode or Docker socket mount. Each run first checks the expected architecture, page size, database loading, and scanner startup. Failure output appears in the container log.

The persisted root remains available for planning against another inventory on the PC. Read-only access for this job is enforced by the container mount.

For a fresh small-folder test, verify that the scan reports `complete`, the hash count matches the test files, and the hash summary reports no skipped or unstable entries. Later runs can skip entries whose valid hashes are already cached.

## Run through SSH and Docker

Alternatively, copy the image archive and configuration to a NAS directory accessible through SSH, then load the image:

```sh
docker load -i backup-normalizer-qnap-arm32-aot.tar
```

Copy the example environment file to `.env` and replace the share paths. From the directory containing the Compose file:

```sh
docker compose up --abort-on-container-exit --exit-code-from backup-normalizer
```

If the NAS has Compose v1, use `docker-compose` with the same arguments. Mount source directories must already exist. Keep the state directory outside the scanned directory.

To run without Compose:

```sh
docker run --rm -t --platform linux/arm/v7 \
  --mount type=bind,source=/share/BackupNormalizerTest,target=/data,readonly \
  --mount type=bind,source=/share/BackupNormalizerState,target=/state \
  --entrypoint /app/qnap-inventory.sh \
  backup-normalizer:qnap-arm32-aot
```

The terminal allocation enables live scan/hash progress. Hashing defaults to one worker for the first NAS test. Increase `BN_HASH_PARALLELISM` only after observing throughput and NAS load.

To run just the compatibility check:

```sh
docker run --rm -t --platform linux/arm/v7 \
  --mount type=bind,source=/share/BackupNormalizerTest,target=/data,readonly \
  --entrypoint /app/qnap-check.sh \
  backup-normalizer:qnap-arm32-aot /app/BackupNormalizer /data
```

## Expand and repeat

After the small-folder job succeeds on the NAS, select the intended share for `/data`. Use a different state directory for that full inventory, so the test database remains separate.

Rerunning the job rescans the same root and reuses valid hashes. A root path inside the container must always represent the same NAS directory for a given state directory. If you change the host directory bound to `/data`, choose a fresh state directory as well. This avoids treating files from a different physical directory as an existing cache.

## Bring the inventory back to the PC

Use `db export` to create a standalone inventory through SQLite's backup API, including committed WAL data. Run with the existing state directory mounted and choose a fresh output filename:

```sh
docker run --rm --platform linux/arm/v7 \
  --mount type=bind,source=/share/BackupNormalizerState,target=/state \
  backup-normalizer:qnap-arm32-aot db export --db /state/nas.db --output /state/portable.db
```

Copy `portable.db` to the PC and open it in the Windows application. Export refuses to overwrite an existing destination. See the [portable export guide](../portable-inventory-export/README.md).

The root paths stored in the NAS database use `/data`. Diff and planning compare inventories and do not need to read NAS content. Execution that later copies source files must use an accessible path through `--source-path`. Use the documented fresh-database import workflow for replaying plans on another root.

## Current verification

Local checks and the image artifact are recorded in the [verification log](verification.md). A local ARM emulator uses the PC kernel and cannot establish compatibility with the NAS's 32 KiB page size.
