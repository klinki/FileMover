# BackupNormalizer

Cross-platform .NET 10 CLI for reconciling file trees spread across multiple disks, minimizing copied bytes and never destroying the last known copy of content.

Full behavior is defined in `backup-normalizer-spec.md`. This README is the practical guide for daily use.

Safety model: scan/plan never touch user files. `plan` is dry-run by default. `execute` requires an explicit plan ID, validates source/destination before each op, copies via `tmp -> flush -> verify -> rename`, moves duplicates to `.backup-normalizer-trash/<plan-id>/` instead of deleting. Permanent delete only via explicit `purge --yes`.

## 1. Run on your main computer (no Docker needed)

Docker is only needed for the QNAP NAS (see §8). On Windows/macOS/Linux desktop, run natively with .NET 10.

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

## 5. Modes and workflows

There are two modes, sharing one plan format. A plan (`plan.json`) is just
logical root IDs + relative paths + expected size/hash — so UI staging and
auto-diff are interchangeable plan *sources*, and the same executor (with
`--map-root` replay) runs both.

### 5.1 Initial organization — auto sync from two snapshots

Two unsorted disks, one of them (or an external layout) defines the desired
state:

```bash
# 1-2. Snapshot both disks (incremental; repeat any time)
bn init --db ./disk1.db
bn root add disk /Volumes/D1 --role Backup --db ./disk1.db
bn scan disk --db ./disk1.db && bn hash --needed --db ./disk1.db

bn init --db ./disk2.db
bn root add disk /Volumes/D2 --role Backup --db ./disk2.db
bn scan disk --db ./disk2.db && bn hash --needed --db ./disk2.db

# 3. Compute operations (disk1's layout wins; swap DBs to reverse direction)
bn plan --canonical-db ./disk1.db --target-db ./disk2.db --target-root disk --plan organize-001
bn plan show organize-001 --db ./disk2.db

# 4. Execute + verify (resume on interruption)
bn execute organize-001 --db ./disk2.db --map-root disk=/Volumes/D2
bn verify organize-001 --db ./disk2.db --map-root disk=/Volumes/D2
```

### 5.2 Fast sync — replay one schedule on an in-sync pair

Assumes disk1 and disk2 are identical. Don't assume it — prove it first
(fresh incremental scans + hash-compared diff, §3):

```bash
# 0. Precondition: prove in-sync (must report no differences)
bn scan disk --db ./disk1.db && bn hash --needed --db ./disk1.db
bn scan disk --db ./disk2.db && bn hash --needed --db ./disk2.db
bn diff --old ./disk1.db --new ./disk2.db

# 1-2. Schedule on disk1: either stage in the UI (F5/F6/F7/F8, Save JSON)
# or auto-diff two snapshots of disk1 — both yield the same plan format.
bn plan import ./ui-plan.json --db ./disk1.db --root-path /Volumes/D1

# 3. Execute + verify on disk1
bn execute <plan-id> --db ./disk1.db --map-root disk=/Volumes/D1
bn verify <plan-id> --db ./disk1.db --map-root disk=/Volumes/D1

# 4-5. Remap ("change the drive letter") and execute + verify on disk2
bn execute <plan-id> --db ./disk1.db --map-root disk=/Volumes/D2
bn verify <plan-id> --db ./disk1.db --map-root disk=/Volumes/D2
```

Notes:

- Drifted ops fail as `Conflict`, never as bad writes. A partially applied
  disk2 just needs a fresh `diff` + re-plan.
- `TRASH` caveat: the never-delete-the-last-copy proof is made by the
  planner against disk1. On replay the executor re-checks the source hash
  but not surviving-copy existence on disk2. Trash is recoverable and
  `purge` is separate, but when in doubt re-plan per disk instead of
  replaying.

### 5.3 Other workflows

- **Single-DB normalization** (§2): all drives attached at once, one DB,
  `plan --canonical <root>`. Simplest when everything is local.
- **UI-manual organization of one disk**: stage in the UI, `Write to DB`,
  execute in place. No second disk involved.
- **Restore/rollback**: keep a T0 snapshot; `plan --canonical-db t0.db`
  restores the known-good layout (§4).
- **NAS-assisted** (§8): scan on the QNAP container (read-only mount),
  import the DB on the desktop, plan there. No hashing over SMB.
- **Audit-only drift detection**: scheduled `scan` + `hash --needed` +
  `diff`, never `execute`. Alert on any difference.
- **Re-plan vs replay rule of thumb**: replay (`--map-root`) while the
  pair is proven in sync and the plan has no `TRASH`; otherwise re-plan —
  planning is cheap, deleting the wrong copy is not.
- **Version your plans**: `plan export` JSON files are small, stable text
  (logical paths + sizes + hashes, no absolute machine paths). Commit them
  to git next to a note of which DB snapshot they were built from:
  ```bash
  bn plan export organize-001 --db ./disk2.db --output ./plans/2026-09-24-organize-001.json
  ```
  Later `diff` + `plan conflicts` output tells you exactly what has drifted
  since. Never commit `.db` files (binary, machine-local); the exported
  JSON plus a fresh scan reproduces everything.

## 6. Command reference

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
plan import <plan.json> [--db PATH] [--root-path ABS] [--role R]
plan conflicts <plan-id> [--db PATH]
execute <plan-id> [--db PATH] [--map-root id=path ...] [--resume] [--stop-on-error] [--yes]
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

Exit codes: `0` ok, `2` usage/error/declined confirmation, `3` execute/verify completed with failures or conflicts.

`execute` prints the plan totals and per-operation `[i/n]` progress, and asks
`Execute N operations (X bytes to copy)? [y/N]` unless `--yes` is given
(automation should always pass `--yes`; a declined or missing answer aborts
with code 2 and modifies nothing).

## 7. Pre-execution checks and conflicts

Before each `MOVE`/`COPY`/`TRASH`, the executor re-validates size/mtime/hash. These stop that op as `Conflict` (other ops continue unless `--stop-on-error`):

- source disappeared or changed since planning (stale plan fails safely)
- destination exists with different bytes (never auto-overwrites; identical destination becomes `KEEP`)
- trash candidate lost its surviving copy or changed hash
- copy source root not mapped (hint shows required `--map-root`)

## 8. QNAP NAS (Docker, `Dockerfile.qnap`)

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

## 9. Tests

```bash
dotnet test BackupNormalizer.slnx
```

Covers path normalization, content grouping, same-size/different-content rejection, source-cost ranking, hash-cache reuse, MOVE-vs-COPY, duplicate handling, stale-plan safety (source disappears/changes, destination occupied), 2-DB diff + `--map-root` migration, trash + resume, inventory export/import roundtrip.
