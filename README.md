# BackupNormalizer

Cross-platform .NET 10 CLI for reconciling file trees spread across multiple disks, minimizing copied bytes and never destroying the last known copy of content.

Full behavior is defined in `backup-normalizer-spec.md`. This README is the practical guide for daily use.

Safety model: scan/plan never touch user files. `plan` is dry-run by default. `execute` requires an explicit plan ID, validates source/destination before each op, copies via `tmp -> flush -> verify -> rename`, moves duplicates to `.backup-normalizer-trash/<plan-id>/` instead of deleting. Permanent delete only via explicit `purge --yes`.

## 1. Run on your main computer (no Docker needed)

Docker is only needed for the QNAP NAS (see §7). On Windows/macOS/Linux desktop, run natively with .NET 10.

Requirements:

- .NET 10 SDK (`dotnet --version` should print `10.x`)
- No other dependencies. SQLite is bundled via `SQLitePCLRaw.bundle_sqlite3`.

Build once:

```bash
dotnet build BackupNormalizer.slnx -c Release
```

Run via:

```bash
dotnet run --project src/BackupNormalizer -- <command>
```

Or publish a self-contained binary (no SDK needed afterwards):

```bash
dotnet publish src/BackupNormalizer -c Release --self-contained true -o ./publish
./publish/BackupNormalizer --version
```

All examples below use `dotnet run --project src/BackupNormalizer --` shortened as `bn`. Replace with your published binary path if you published.

## 2. Quickstart: external drives D1, D2 on one machine

This is the standard single-DB mode: one SQLite file tracks all roots.

```bash
# 1. Create inventory DB + config
bn init --db ./backup-normalizer.db

# 2. Register drives (examples - use your actual mount paths)
# macOS:
bn root add d1 /Volumes/D1 --role Backup --db ./backup-normalizer.db
bn root add d2 /Volumes/D2 --role Backup --db ./backup-normalizer.db
# Windows:
# bn root add d1 E:\ --role Backup --db ./backup-normalizer.db
# bn root add d2 F:\ --role Backup --db ./backup-normalizer.db
# Linux:
# bn root add d1 /mnt/d1 --role Backup --db ./backup-normalizer.db
# bn root add d2 /mnt/d2 --role Backup --db ./backup-normalizer.db

# Canonical tree = desired layout, e.g. NAS staging dir or one of the drives:
bn root add nas /Volumes/NAS-Backup --role Canonical --db ./backup-normalizer.db

bn root list --db ./backup-normalizer.db

# 3. Scan cheap metadata only (no hashing yet)
bn scan --all --db ./backup-normalizer.db
# or per-drive:
# bn scan d1 --db ./backup-normalizer.db

# 4. Hash only what planner needs (incremental, cached by size+mtime)
bn hash --needed --db ./backup-normalizer.db --parallelism 2
# force full rehash of one root:
# bn hash d1 --all --db ./backup-normalizer.db

# 5. Dry-run plan (modifies nothing)
bn plan --canonical nas --db ./backup-normalizer.db --plan 2026-09-24-001

# 6. Inspect + export
bn plan show 2026-09-24-001 --db ./backup-normalizer.db
bn plan export 2026-09-24-001 --db ./backup-normalizer.db --output plan.json

# 7. Execute approved plan, then verify
bn execute 2026-09-24-001 --db ./backup-normalizer.db
bn verify 2026-09-24-001 --db ./backup-normalizer.db

# Resume after interruption (only retries non-completed, re-validates):
bn execute 2026-09-24-001 --db ./backup-normalizer.db --resume

# Trash is recoverable. Purge only when you mean it:
bn purge --older-than 30d --yes --db ./backup-normalizer.db
```

What planner prefers: `KEEP (0) > same-filesystem MOVE (metadata-only) > COPY (file size) > TRASH`. `COPY` source selection prefers `Canonical > Backup > Archive > Temporary`, then shorter local paths. Same-size/different-hash files are never grouped. Files without a full hash are never trashed.

## 3. Per-drive separate DBs + 2-file diff (your T0/T1 mode)

Run the scanner once per drive, each producing its own `.db`. No central DB needed at scan time. Ideal when drives are attached at different times or scanned on different machines.

```bash
# On main computer, one DB per drive:
bn init --db ./d1.db
bn root add d1 /Volumes/D1 --role Backup --db ./d1.db
bn scan d1 --db ./d1.db
bn hash --needed --db ./d1.db

bn init --db ./d2.db
bn root add d2 /Volumes/D2 --role Backup --db ./d2.db
bn scan d2 --db ./d2.db
bn hash --needed --db ./d2.db

# Compare two DB files directly:
bn diff --old ./d1.db --new ./d2.db
# output: only-in-old / only-in-new / changed / identical + samples
```

Merge into a central DB when you want global planning:

```bash
bn init --db ./central.db
bn inventory import ./d1.db --db ./central.db
bn inventory import ./d2.db --db ./central.db
bn root list --db ./central.db

# Or export one root for transport (e.g. from NAS):
bn inventory export nas --db ./central.db --output ./nas.inventory.db
```

## 4. T0 snapshot vs T1 current + plan migration via root remap

```bash
# T0: baseline snapshot (e.g. last month, or known-good layout)
bn init --db ./t0.db
bn root add disk /Volumes/D1 --role Backup --db ./t0.db
bn scan disk --db ./t0.db
bn hash --needed --db ./t0.db

# ... time passes, files move ...

# T1: current state of same (or another) drive
bn init --db ./t1.db
bn root add disk /Volumes/D1 --role Backup --db ./t1.db
bn scan disk --db ./t1.db
bn hash --needed --db ./t1.db

# What changed?
bn diff --old ./t0.db --new ./t1.db

# Make T1 look like T0 (T0 = canonical/desired):
bn plan --canonical-db ./t0.db --target-db ./t1.db --target-root disk --plan restore-001
bn plan show restore-001 --db ./t1.db

# Execute in place:
bn execute restore-001 --db ./t1.db --map-root disk=/Volumes/D1

# Migrate the same layout to a different drive (plan remap):
# Scan the new drive into its own DB, then re-plan against T0 and remap:
bn init --db ./d2.db
bn root add disk /Volumes/D2 --role Backup --db ./d2.db
bn scan disk --db ./d2.db
bn hash --needed --db ./d2.db
bn plan --canonical-db ./t0.db --target-db ./d2.db --target-root disk --plan migrate-001
bn execute migrate-001 --db ./d2.db --map-root disk=/Volumes/D2
```

Notes:

- `--canonical-root <id>` optionally limits the canonical DB to one root. Default uses all roots in the canonical DB as desired paths.
- `canon:*` sources in a plan are offline snapshot content: if the bytes aren't reachable at execute time, that op fails safely as `Conflict` instead of writing bad data.
- Plans are immutable: re-running `plan` with an existing ID errors. Generate a new plan ID if state changed.
- `--map-root` can be repeated: `--map-root disk=/Volumes/D2 --map-root nas=/Volumes/NAS`.

## 5. Command reference

```
init [--db PATH] [--config PATH]
root add <id> <path> [--role Canonical|Backup|Archive|Temporary|Unknown] [--name N] [--writable true|false] [--db PATH]
root list [--db PATH]
scan <rootId|--all> [--db PATH]
hash --needed [--db PATH] [--parallelism N]
hash <rootId> --all [--db PATH]
plan --canonical <rootId> [--db PATH] [--plan ID]
plan --canonical-db C.db --target-db T.db [--canonical-root R] --target-root R [--plan ID]
plan show <plan-id> [--db PATH]
plan export <plan-id> [--format json] [--output F] [--db PATH]
execute <plan-id> [--db PATH] [--map-root id=path ...] [--resume] [--stop-on-error]
verify <plan-id> [--db PATH] [--map-root id=path ...]
purge --older-than 30d --yes [--db PATH] [--path ROOTPATH]
inventory export <rootId> --output F [--db PATH]
inventory import <file> [--db PATH]
diff --old A.db --new B.db
db-test | scan-test <path> | --version
```

Config file `backup-normalizer.json` (created by `init`, overridable via `--config` or `BN_CONFIG` env):

```json
{
  "database": "./backup-normalizer.db",
  "hashAlgorithm": "sha256",
  "hashParallelism": 2,
  "copyParallelism": 1,
  "trashDirectoryName": ".backup-normalizer-trash",
  "roots": []
}
```

`hashAlgorithm` accepts `sha256` (current) and `blake3` (mapped to SHA-256 fallback with a warning until a validated BLAKE3 native is added for ARM32/QNAP). Hashing is streaming (4 MiB buffer), supports >4 GB files, never loads whole files.

Exit codes: `0` ok, `2` usage/error, `3` execute/verify completed with failures or conflicts.

## 6. Pre-execution checks and conflicts

Before each `MOVE`/`COPY`/`TRASH`, the executor re-validates size/mtime/hash. These stop that op as `Conflict` (other ops continue unless `--stop-on-error`):

- source disappeared or changed since planning (stale plan fails safely)
- destination exists with different bytes (never auto-overwrites; identical destination becomes `KEEP`)
- trash candidate lost its surviving copy or changed hash
- copy source root not mapped (hint shows required `--map-root`)

## 7. QNAP NAS (Docker, `Dockerfile.qnap`)

Use this only for the QNAP TS-431P3 (ARMv7, 32 KB page size). Desktops run natively per §1.

Build on your main computer with buildx:

```bash
docker buildx build --platform linux/arm/v7 -f Dockerfile.qnap -t backup-normalizer:qnap-arm32 .
```

Run scan-only with read-only source mount (spec §4 example):

```yaml
services:
  backup-normalizer:
    image: backup-normalizer:qnap-arm32
    volumes:
      - /share/Backup:/data:ro
      - /share/BackupNormalizer:/state
```

```bash
# inside container / on NAS via container exec:
./BackupNormalizer db-test
./BackupNormalizer scan-test /data
./BackupNormalizer init --db /state/nas.db
./BackupNormalizer root add nas /data --role Canonical --db /state/nas.db
./BackupNormalizer scan nas --db /state/nas.db
./BackupNormalizer hash --needed --db /state/nas.db --parallelism 2
```

Copy `/state/nas.db` (or `inventory export nas --output nas.inventory.db`) back to your main computer, then `inventory import` + `plan` + `diff` there. This avoids hashing NAS files over SMB. The image uses `alpine:3.17` + system `sqlite-libs` + `linux-musl-arm` self-contained publish for the 32 KB page-size environment. Before relying on it, verify on the NAS:

```bash
uname -m          # expect armv7l
getconf PAGESIZE  # expect 32768
./BackupNormalizer db-test
./BackupNormalizer scan-test /data
```

## 8. Tests

```bash
dotnet test BackupNormalizer.slnx
```

Covers path normalization, content grouping, same-size/different-content rejection, source-cost ranking, hash-cache reuse, MOVE-vs-COPY, duplicate handling, stale-plan safety (source disappears/changes, destination occupied), 2-DB diff + `--map-root` migration, trash + resume, inventory export/import roundtrip.
