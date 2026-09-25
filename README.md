# BackupNormalizer

BackupNormalizer compares two file trees and can plan changes that make the target match the source. It uses .NET 10, EF Core, and SQLite. The command line tool runs on Windows, macOS, Linux, and QNAP Linux. The Avalonia desktop UI stages manual file operations.

The source and target are choices for each comparison. Neither database nor root has a permanent role. Keep one inventory database per drive if that makes the files easier to manage; each database can contain several named roots.

## Build

```bash
dotnet build BackupNormalizer.slnx -c Release
dotnet test BackupNormalizer.slnx -c Release
```

The examples use `bn` as shorthand for `dotnet run --project src/BackupNormalizer --` or a published `BackupNormalizer` binary.

## Compare and plan

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

`plan` only writes operations to the target database. It keeps identical target files, moves matching target files to desired paths, and copies bytes from the source when needed. It moves an extra target file to recoverable trash only when the target has another verified copy of its content. A file at a desired path with different content becomes a conflict; the planner does not overwrite it. Empty directories are outside the inventory.

## Execute

Review the plan before running it. `execute` asks for confirmation unless you pass `--yes`.

```bash
bn execute photos-001 --db ./target.db
bn verify photos-001 --db ./target.db
bn plan conflicts photos-001 --db ./target.db
```

If a drive is mounted elsewhere, supply its current path. The source database file is not needed to execute a saved plan, but the source files must be available for operations that copy from it.

```bash
bn execute photos-001 --db ./target.db --source-path /new/source/photos --target-path /new/target/photos --resume
bn verify photos-001 --db ./target.db --target-path /new/target/photos
```

The executor checks sizes and full hashes before moving or trashing files. Copies go through a temporary file, flush, hash check, and rename. Target files with unexpected content are left in place. Trash is stored under `.backup-normalizer-trash/<plan-id>/` inside the target root. Permanent deletion requires a separate `purge --yes` command.

## Database format

EF Core applies migrations when it opens a writable database. The current initial migration creates `StorageRoot`, `Scan`, `FileEntry`, `FileHash`, `Plan`, `PlanOperation`, and `ExecutionLog`. A complete rescan marks files that disappeared as missing. An incomplete scan never removes stale entries and cannot be used for planning.

This is the first release schema. Databases created by the earlier pre-release schema with root roles must be recreated. The application detects them and reports an error without modifying them. Later EF migrations will apply to current-schema databases.

## Desktop planner

Run `dotnet run --project src/BackupNormalizer.Ui` to stage manual copy, move, folder, and trash operations. The UI only writes a plan. See the [desktop planner guide](src/BackupNormalizer.Ui/README.md) for its controls and CLI execution steps.

## QNAP

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
