# BackupNormalizer

BackupNormalizer compares two file trees and can plan changes that make the target match the source. It uses .NET 10, EF Core, and SQLite. The command line tool runs on Windows, macOS, Linux, and QNAP Linux. The Avalonia desktop UI stages manual file operations.

The source and target are choices for each comparison. Neither database nor root has a permanent role. Keep one inventory database per drive if that makes the files easier to manage; each database can contain several named roots.

## Build

```bash
dotnet build BackupNormalizer.slnx -c Release
dotnet test BackupNormalizer.slnx -c Release
```

### Formatting

The repository pins [CSharpier](https://csharpier.com/) as a local .NET tool.
It uses [.editorconfig](.editorconfig) for four-space indentation, LF line endings,
and a 100-column wrapping target. From the repository root:

```bash
dotnet tool restore
dotnet csharpier format .
dotnet csharpier check .
```

Use `format` to apply the layout and `check` to verify it without changing files.
Formatting covers handwritten C# in the application and tests. Generated EF Core
migrations and XML files are excluded.

The examples use `bn` as shorthand for `dotnet run --project src/BackupNormalizer --` or a published `BackupNormalizer` binary.

Build identity and self-contained CLI/UI release ZIPs are documented in the
[versioned release guide](docs/features/versioned-releases/README.md).

Inspect an inventory with `bn status [rootId] --db inventory.db`, retrieve failures
with `bn scan errors <rootId> --db inventory.db`, or export a standalone copy with
`bn db export --db inventory.db --output portable.db`. See the
[inventory diagnostics guide](docs/features/inventory-diagnostics/README.md) and
[portable export guide](docs/features/portable-inventory-export/README.md).

## Compare and plan

JSON defaults and root-relative regex exclusions are described in the
[CLI configuration guide](docs/features/cli-config-exclusions/README.md).

Register a directory in each database, scan it, and hash its files:

```bash
bn init --db ./source.db
bn root add photos /mnt/source/photos --db ./source.db
bn scan photos --db ./source.db
bn hash photos --all --db ./source.db

bn init --db ./target.db
bn root add photos /mnt/target/photos --db ./target.db
bn scan photos --db ./target.db
bn hash photos --all --db ./target.db
```

The root IDs can be the same in the two databases. You can also select two disjoint roots in one database. Run a diff, then create a plan in the target database:

```bash
bn diff --source-db ./source.db --source-root photos --target-db ./target.db --target-root photos
bn plan --source-db ./source.db --source-root photos --target-db ./target.db --target-root photos --plan photos-001
bn plan show photos-001 --db ./target.db
bn plan export photos-001 --db ./target.db --output ./photos-001.json
```

The source root defines the desired paths for that run. To reverse the direction, swap the source and target arguments and scan both roots again first. Diff reports source-only, target-only, changed, identical, and unverified files. It can compare historical snapshots of the same directory. Automatic planning requires two disjoint directories and a complete scan of each root.

The inventory database may live inside its scanned root. Scanning and hashing exclude that database and its SQLite companions. Other database files are ordinary inventory content. Rescanning also retires entries for the active database left by older scans.

### Hashing progress

In a terminal, `hash` shows completed files and percentage, bytes read, reading speed, elapsed time, and the current path. Byte counts update while a large file is being read. The percentage counts inventory entries processed, including reused and skipped entries; it is not a byte percentage or time estimate.

`hash <root-id> --all` recomputes every available file's hash. `hash --needed` reuses valid cached hashes across the database. Both accept `--parallelism N` (default `2`) and `--no-progress`. Redirected output contains only the final summary.

### Fast NTFS scanning (Windows only, opt-in)

On large NTFS drives, `scan` can read the Master File Table directly instead
of walking directories (sizes and timestamps included, no per-file stat
calls). It needs an NTFS volume and administrator rights, and it is off by
default:

```bash
bn scan photos --db ./source.db --mft auto     # use when available, else fall back
bn scan photos --db ./source.db --mft require  # fail loudly when unavailable
bn --elevate scan photos --db ./source.db --mft auto  # restart elevated via UAC first
```

`--mft` can also be set persistently with `"mftMode": "auto"` in
`settings.json`. Hashing still reads every file, so this only
accelerates the metadata pass. Validate once per drive by scanning both ways
and comparing the inventories. Unexpected read or parsing failures after an
MFT scan starts fail that scan without marking unseen files missing. Retry
with `--mft off`; `auto` falls back only when MFT initialization is unavailable.

### Incremental NTFS rescans

Scanning defaults to `--usn auto`. On a local NTFS volume with an existing USN
change journal and sufficient access, the first successful scan stores a
checkpoint. Later scans refresh changed file and link entries and mark deleted
paths missing, preserving untouched inventory and hashes. The summary reports
entries refreshed using USN, rather than the total inventory size. Hashing stays
a separate step:

```bash
bn --elevate scan photos --db ./source.db
bn hash --needed --db ./source.db
bn scan photos --db ./source.db --full      # enumerate everything again
bn scan photos --db ./source.db --usn off  # full scan without a journal checkpoint
```

Set `"usnMode": "off"` in `settings.json` to disable journal use by
default. Journal access normally requires administrator rights. The app reads
existing journals and does not create them. Other filesystems, network shares,
and unavailable journals use full scans; `--mft` selects how those full scans
enumerate NTFS metadata.

Journal resets, lost history, moved or replaced roots, incomplete scans,
directory namespace or link changes, hard-link changes, and ambiguous paths
trigger a full scan. An untrustworthy change window also invalidates cached
hashes conservatively, so the next `hash --needed` can require reading otherwise
unchanged files. Known content writes invalidate hashes even when size and
timestamp match the previous scan. Checkpoints advance only with successful
inventory updates. Older databases establish their first checkpoint through a
full scan; they do not become complete merely by upgrading.

See the [implementation plan](docs/features/usn-incremental-scans/implementation-plan.md)
and [delivery notes](docs/features/usn-incremental-scans/delivery.md) for scope and verification.

`plan` only writes operations to the target database. It keeps identical target files, moves matching target files to desired paths, and copies bytes from the source when needed. It moves an extra target file to recoverable trash only when the target has another verified copy of its content. A file at a desired path with different content becomes a conflict; the planner does not overwrite it. Empty directories are outside the inventory.

## Execute

Review the plan before running it. `execute` asks for confirmation unless you pass `--yes`.

```bash
bn execute photos-001 --db ./target.db
bn verify photos-001 --db ./target.db
bn plan conflicts photos-001 --db ./target.db
```

Before the first execution, supply the current paths if the drives are mounted elsewhere. The source database file is not needed to execute a saved plan, but the source files must be available for operations that copy from it.

```bash
bn execute photos-001 --db ./target.db --source-path /new/source/photos --target-path /new/target/photos
bn execute photos-001 --db ./target.db --resume
bn verify photos-001 --db ./target.db
```

The first execution records its effective roots. Retries, resume, and verification default to those paths. Execution rejects changing roots in a database that already has an execution binding, so completed operations cannot be silently skipped on a different drive. Verification still accepts explicit path overrides.

To organize files on a PC under `G:\Photos` and replay the exported relative operations on an identical external drive under `E:\Photos`, import the original JSON into a fresh execution database:

```powershell
bn plan import .\ui-plan.json --db .\external-plan.db --target-path "E:\Photos"
bn execute <plan-id> --db .\external-plan.db
bn verify <plan-id> --db .\external-plan.db
```

Use a separate execution database for each replay. Imported plans start with fresh operation statuses; content drift still causes conflicts. Plans attempted before execution-root recording was introduced must also be imported into a fresh database for further execution. Their existing history is preserved.

The executor checks sizes and full hashes before moving or trashing files. Copies go through a temporary file, flush, hash check, and rename. Target files with unexpected content are left in place. Trash is stored under `.backup-normalizer-trash/<plan-id>/` inside the target root. Permanent deletion requires a separate `purge --yes` command.

## Database format

EF Core applies migrations when it opens a writable database. The current initial migration creates `StorageRoot`, `Scan`, `FileEntry`, `FileHash`, `Plan`, `PlanOperation`, and `ExecutionLog`. A complete rescan marks files that disappeared as missing. An incomplete scan never removes stale entries and cannot be used for planning.

Scans retain file symlinks, directory symlinks, and junctions with their original target text and absolute immediate target path. They do not traverse linked directories or hash linked contents. Broken targets and unavailable link metadata produce notes rather than scan errors. Read-only inventories from before this migration remain browsable; open the database writable and rescan to populate link metadata and replace an older incomplete scan result.

Links and paths blocked by links appear as `SKIP_LINK` operations with a reason in exported plans. Execution reports them as skipped and continues with other files. It also checks for links introduced after scanning, including linked parent directories. Copying external targets and recreating links are deferred.

This is the first release schema. Databases created by the earlier pre-release schema with root roles must be recreated. The application detects them and reports an error without modifying them. Later EF migrations will apply to current-schema databases.

## Desktop planner

The planned [file location changes report](docs/features/file-location-changes/feature-spec.md)
will compare inventory snapshots and distinguish unique moves, retained originals
with new copies, and ambiguous duplicate matches. Its specification is saved;
the dedicated report is not implemented yet.

Compare content across several disks with [backup coverage](docs/features/backup-coverage/README.md),
available through `bn coverage` and **Inventory → Backup coverage...** in the desktop app.

Run `dotnet run --project src/BackupNormalizer.Ui` to stage manual copy, move, folder, and trash operations. Review and execute staged or database-generated plans in the desktop app, with progress, cancellation, saved execution history, resume, and destination verification. See the [desktop guide](src/BackupNormalizer.Ui/README.md) and [execution guide](docs/features/gui-execution/README.md).

## QNAP

Use the [NAS container guide](docs/features/nas-container/README.md) to build an ARMv7 image, import it into QNAP Container Station, and run a one-shot inventory with read-only data and persistent state. The repository targets the TS-431P3's 32 KiB kernel page size; actual NAS compatibility still requires its on-device checks.

Publish for the NAS architecture you use. For a 32-bit ARM QNAP running musl Linux:

```bash
dotnet publish src/BackupNormalizer/BackupNormalizer.csproj -c Release -r linux-musl-arm --self-contained true -o ./publish/qnap-arm
```

Copy the published files to the NAS and run `db-test` and `scan-test` there before a full scan:

```bash
./BackupNormalizer db-test
./BackupNormalizer scan-test /data
./BackupNormalizer init --db /state/nas.db
./BackupNormalizer root add data /data --db /state/nas.db
./BackupNormalizer scan data --db /state/nas.db
./BackupNormalizer hash data --all --db /state/nas.db
```

Copy `/state/nas.db` to the computer where you plan. This avoids hashing NAS files over a network mount. A plan that copies from `/data` still needs those files accessible to the executor, through a mount or `--source-path`.

The [approved per-drive design](docs/features/per-drive-root-comparison/implementation-plan.md) describes the current workflow. The [original specification](backup-normalizer-spec.md) remains as historical design notes and describes some commands and schema fields that no longer exist.
