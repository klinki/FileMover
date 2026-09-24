# Backup Normalizer — Implementation Specification

## 1. Purpose

Backup Normalizer is a cross-platform .NET application for reconciling and normalizing file trees that are spread across multiple disks or storage devices.

Typical environment:

- NAS volume `N`
- External disk `D1`
- External disk `D2`
- One canonical/source-of-truth file tree describing where files should ultimately live
- Existing files may already exist on the correct device but under the wrong path
- Duplicate copies may exist on several devices
- Large files may make unnecessary copying expensive

The primary optimization goal is:

> Transform the current physical file layout into the desired canonical layout while minimizing transferred file data and avoiding unnecessary reads.

The primary safety goal is:

> Never destroy the only known copy of file content, and never perform destructive operations unless they were explicitly planned, verified, and approved.

The tool must treat scanning, planning, execution, and verification as separate phases.

---

## 2. High-level architecture

The application should consist of these logical components:

1. **Scanner**
   - Enumerates files on a storage root.
   - Stores file metadata in SQLite.
   - Calculates hashes only when needed.
   - Supports incremental rescans.

2. **Inventory database**
   - Stores the physical state of all known storage roots.
   - Stores content identity information.
   - Stores scan history.
   - Stores generated plans and execution history.

3. **Canonical tree provider**
   - Defines the desired logical path of each file.
   - Initially, the canonical tree may simply be one designated source tree.
   - The design should allow future canonical-tree providers.

4. **Matcher**
   - Identifies files that are definitely identical, probably identical, or unrelated.

5. **Planner**
   - Calculates the cheapest safe transformation from current state to canonical state.
   - Strongly prefers same-filesystem moves/renames over copying.

6. **Executor**
   - Applies an approved plan.
   - Journals every operation.
   - Supports restart/resume after interruption.

7. **Verifier**
   - Confirms that operations produced the expected result.
   - Rechecks hashes when appropriate.

---

## 3. Technology choices

### 3.1 Runtime

Use:

- C#
- .NET 10 LTS
- Console/CLI application

Primary development environments:

- Windows x64
- macOS ARM64
- Linux x64/ARM64
- Linux ARM32 (`linux-arm` / `linux-musl-arm`)

The core project should avoid dependencies that prevent ARM32 publishing.

### 3.2 Database

Use SQLite.

Use EF Core with its SQLite provider and bundled native SQLite library on all targets. Create the schema directly for this first version; add a migration strategy before schema upgrades must preserve existing databases. Validate the `linux-musl-arm` publish on the actual QNAP before treating it as supported.

The database should be treated as an inventory/cache and operation journal. It is not the source of truth for file contents.

### 3.3 Hashing

Preferred full-file hash:

- BLAKE3

Fallback acceptable hash:

- SHA-256

Do not use file name as content identity.

Do not treat size alone as definitive identity.

MD5 is not required.

The hash abstraction should allow changing algorithms later.

---

## 4. Target storage environment

Important target NAS:

- QNAP TS-431P3
- Linux
- ARMv7 / ARM32
- 32 KB OS memory page size
- Docker support

The QNAP deployment should use a container compatible with the NAS's 32 KB page-size environment.

Preferred initial container baseline:

- Alpine Linux 3.17 ARMv7
- .NET 10 `linux-musl-arm`
- system `libsqlite3`

The NAS scanner must be testable independently from the desktop planner.

During scan-only operation, source volumes should preferably be mounted read-only.

Example:

```yaml
services:
  backup-normalizer:
    image: backup-normalizer:qnap-arm32
    volumes:
      - /share/Backup:/data:ro
      - /share/BackupNormalizer:/state
```

The application must not assume a 4 KB OS page size.

---

## 5. Core concepts

The implementation should keep these concepts strictly separate.

### 5.1 Physical state

What currently exists on disks.

Example:

```text
N:/OldPhotos/a.jpg
D1:/Backup/Photos/a.jpg
D2:/a.jpg
```

### 5.2 Canonical state

Where content should exist after normalization.

Example:

```text
N:/Photos/2024/a.jpg
```

### 5.3 Content identity

The actual byte content of a file.

Example:

```text
BLAKE3 = abc123...
```

All files with that hash and size represent the same content.

### 5.4 Operation plan

The minimal safe set of operations required to transform physical state into canonical state.

Example:

```text
MOVE N:/OldPhotos/a.jpg -> N:/Photos/2024/a.jpg
DELETE D2:/a.jpg
KEEP D1:/Backup/Photos/a.jpg
```

---

## 6. Storage roots

A storage root represents one mounted tree.

Examples:

```text
N
D1
D2
```

Each root must have:

- stable identifier
- friendly name
- absolute path
- filesystem/device identity where detectable
- writable/read-only setting
- role
- optional canonical-tree role

Suggested roles:

- `Canonical`
- `Backup`
- `Archive`
- `Temporary`
- `Unknown`

Example configuration:

```json
{
  "roots": [
    {
      "id": "nas",
      "name": "N",
      "path": "/data",
      "role": "Canonical",
      "writable": true
    },
    {
      "id": "disk1",
      "name": "D1",
      "path": "E:\\",
      "role": "Backup",
      "writable": true
    }
  ]
}
```

Do not use drive letters or mount paths as permanent database identity.

---

## 7. Scanning

### 7.1 Initial scan

The initial scan should collect cheap metadata only.

For each file:

- storage root ID
- relative path
- file name
- extension
- size
- modified timestamp
- creation timestamp when available
- filesystem identity metadata when available
- scan ID
- hash state
- error state

Do not hash every file during the first metadata pass.

### 7.2 Path rules

Internally store relative paths using a normalized separator:

```text
/
```

Preserve the original case.

Do not assume case sensitivity.

The system must know whether each root's filesystem is case-sensitive.

### 7.3 Symbolic links / junctions

Default behavior:

- do not follow symbolic links
- record the link as a link entry if practical

Following links must require an explicit option.

The scanner must prevent recursive loops.

### 7.4 Files changing during scan

Record:

- size before hash
- modified timestamp before hash
- size after hash
- modified timestamp after hash

If these differ, mark the file as unstable and do not trust the calculated hash.

### 7.5 Permission and I/O errors

Scanning must continue when individual files cannot be read.

Store the error in the database.

Example states:

- `Ok`
- `AccessDenied`
- `NotFound`
- `IoError`
- `ChangedDuringScan`
- `UnsupportedEntry`

---

## 8. Incremental scanning

The database must be reusable.

A file's previously calculated hash may be reused when all relevant cheap metadata matches.

Minimum hash-reuse key:

- same storage root
- same path
- same size
- same last-modified timestamp

Optionally include:

- file identity/inode/file ID when available

If these match, reuse the previous full hash.

If any important field changes, mark the hash stale.

This allows later scans to mostly enumerate metadata instead of rereading all file content.

---

## 9. Hashing strategy

Hashing should be demand-driven.

### 9.1 Matching stages

Use progressively more expensive checks.

#### Stage 1 — Size

Different size means different content.

```text
size A != size B
=> definitely different
```

#### Stage 2 — Optional partial fingerprint

For large ambiguous files, optionally calculate a fast fingerprint from:

- beginning block
- middle block
- ending block

Example block size:

```text
1 MiB
```

This is only a rejection filter.

A partial fingerprint must never be considered definitive identity.

#### Stage 3 — Full hash

Calculate BLAKE3 over the complete file.

Definitive match condition:

```text
same size
AND
same full hash
```

### 9.2 Hash cache

Store:

- algorithm
- digest
- timestamp calculated
- file size at hash time
- modified timestamp at hash time

---

## 10. Canonical tree

Version 1 should support a simple canonical mode:

> One configured tree represents the desired path structure.

For example:

```text
N:/Canonical
```

Every relative path below that tree represents a desired file location.

However, the design must not make "currently located in canonical tree" equivalent to "content must be copied from there".

If identical content exists elsewhere, that copy may be used to satisfy the canonical path.

Future extension:

- manifest-defined canonical tree
- merged canonical trees
- user-defined path mapping rules
- content collections independent of physical device

---

## 11. Matching logic

The matcher should build content groups.

Example:

```text
ContentGroup ABC

N:/Old/a.jpg
D1:/Photos/a.jpg
D2:/random/file1.bin
```

If all three have:

```text
same size
same full hash
```

they are identical content despite different names and paths.

A content group should record:

- size
- hash algorithm
- full hash
- all physical copies

---

## 12. Planning goals

The planner should minimize cost while preserving safety.

The preferred operation priority is:

1. `KEEP`
2. same-filesystem `MOVE`
3. same-filesystem `RENAME`
4. hard-link/reflink optimization if explicitly supported later
5. `COPY`
6. `DELETE`

Conceptually assign costs:

```text
KEEP                  ~ 0
same-filesystem MOVE  ~ metadata-only
COPY                   ~ file size transferred
DELETE                 ~ low transfer cost but high safety cost
```

The planner's main numerical objective is:

```text
minimize total bytes copied between filesystems
```

Secondary objectives:

- minimize number of operations
- avoid unnecessary hashing
- avoid destructive operations
- prefer local source copies over remote/network copies

---

## 13. MOVE detection

If a canonical file is missing at its desired path but identical content already exists elsewhere on the same filesystem, generate a move.

Example:

Current:

```text
N:/OldBackup/Movies/a.mkv
```

Desired:

```text
N:/Movies/a.mkv
```

Plan:

```text
MOVE N:/OldBackup/Movies/a.mkv
  -> N:/Movies/a.mkv
```

Do not generate:

```text
COPY N:/OldBackup/Movies/a.mkv -> N:/Movies/a.mkv
DELETE N:/OldBackup/Movies/a.mkv
```

when an atomic/same-filesystem move is possible.

Determine same-filesystem status using actual device/filesystem identity where possible, not merely the configured root.

---

## 14. COPY source selection

When a canonical file is missing and no same-filesystem copy can be moved, choose a source copy.

Preferred source order:

1. local same-machine drive
2. directly attached storage
3. NAS-local content when planner runs on NAS
4. remote/network source

The source-selection implementation should expose a cost abstraction.

Initial cost may simply be:

```text
copy cost = file size
```

Future cost model may account for:

- network speed
- USB speed
- source drive type
- destination drive type
- NAS vs local computer
- user-configured weights

---

## 15. Duplicate handling

A duplicate may only be considered safely removable if:

- size matches
- full hash matches
- at least one verified desired copy exists or will exist after the plan
- the removal does not violate configured redundancy rules

Do not delete based only on:

- path
- file name
- size
- timestamps
- partial fingerprint

---

## 16. Delete semantics

Version 1 should not permanently delete files by default.

A planned `DELETE` should execute as:

```text
MOVE -> .backup-normalizer-trash/<plan-id>/...
```

when possible.

The trash location should remain on the same filesystem when possible so the operation is metadata-only.

Example:

```text
N:/.backup-normalizer-trash/2026-09-24-001/Old/a.jpg
```

Permanent deletion must require a separate command:

```text
purge
```

The purge command must never be implicitly run as part of normalization.

---

## 17. Operation types

Required operations:

### KEEP

No change required.

### MKDIR

Create destination directory.

### MOVE

Move or rename within the same filesystem.

### COPY

Copy file content between locations/filesystems.

### TRASH

Move obsolete content to tool-managed trash.

### DELETE

Permanent delete.

Only generated by explicit purge workflow.

### VERIFY

Verify an expected file after another operation.

---

## 18. Plan format

Plans must be persisted in SQLite and exportable as JSON.

Example:

```json
{
  "planId": "2026-09-24-001",
  "createdUtc": "2026-09-24T20:00:00Z",
  "estimatedBytesCopied": 734003200,
  "operations": [
    {
      "id": 1,
      "type": "MOVE",
      "sourceRoot": "nas",
      "sourcePath": "Old/a.jpg",
      "destinationRoot": "nas",
      "destinationPath": "Photos/a.jpg",
      "expectedSize": 1837263,
      "expectedHash": "..."
    },
    {
      "id": 2,
      "type": "COPY",
      "sourceRoot": "disk1",
      "sourcePath": "Movie.mkv",
      "destinationRoot": "nas",
      "destinationPath": "Movies/Movie.mkv",
      "expectedSize": 734003200,
      "expectedHash": "..."
    }
  ]
}
```

A plan must be immutable after approval.

If state changes, generate a new plan.

---

## 19. Dry-run

All planning must default to dry-run.

Example:

```bash
backup-normalizer plan --canonical nas
```

Output:

```text
Plan 2026-09-24-001

KEEP      18,422 files
MOVE       1,204 files
COPY          92 files
TRASH        843 files

Bytes already in correct filesystem: 2.84 TB
Bytes moved via metadata operation:   487 GB
Bytes requiring actual copying:       73.4 GB

No files were modified.
```

Execution must require an explicit plan identifier.

Example:

```bash
backup-normalizer execute 2026-09-24-001
```

---

## 20. Pre-execution validation

Immediately before applying each destructive or copying operation, validate assumptions.

### MOVE

Verify source still has expected:

- size
- modified timestamp or identity
- optionally full hash

Verify destination does not contain unexpected content.

### COPY

Verify source.

If destination already exists:

- if identical, convert to `KEEP`
- otherwise stop that operation and report conflict

### TRASH

Verify the content group still has another valid retained copy.

If not, refuse.

---

## 21. Copy implementation

Copies must be crash-safe.

Recommended sequence:

```text
destination.tmp
    ↓
copy
    ↓
flush
    ↓
verify
    ↓
atomic rename to final name
```

Temporary file naming example:

```text
.filename.backup-normalizer.<operation-id>.tmp
```

If interrupted, the executor should detect and resume or clean up incomplete temporary files.

---

## 22. Copy verification

For every copied file:

1. verify size
2. calculate destination full hash
3. compare against expected hash

Only after successful verification should the operation be marked complete.

For very large datasets, an optional performance mode may later relax this, but strict verification should be the default.

---

## 23. Move verification

After move:

- destination exists
- source no longer exists
- destination matches expected file metadata/content identity

Same-filesystem moves should not require rereading entire file content if reliable filesystem identity/hash cache proves it is the same file.

---

## 24. Resume and crash recovery

Execution must be resumable.

Each operation has states:

```text
Planned
Started
Completed
Failed
Skipped
Conflict
```

Write `Started` before performing the filesystem operation.

Write `Completed` only after verification.

On restart:

```bash
backup-normalizer execute <plan-id> --resume
```

The executor should inspect any `Started` operation and determine whether:

- it already completed
- it should be retried
- it is in conflict

Never blindly repeat a destructive operation.

---

## 25. SQLite schema

Exact schema may evolve, but the implementation should include equivalent entities.

### StorageRoot

```text
Id
Name
Path
Role
Writable
FileSystemId
CaseSensitivity
CreatedUtc
```

### Scan

```text
Id
StorageRootId
StartedUtc
CompletedUtc
Status
```

### FileEntry

```text
Id
StorageRootId
RelativePath
Name
Size
ModifiedUtc
CreatedUtc
FileIdentity
LastSeenScanId
Status
Error
```

Unique constraint:

```text
(StorageRootId, RelativePath)
```

### FileHash

```text
FileEntryId
Algorithm
Digest
SizeAtHash
ModifiedUtcAtHash
CalculatedUtc
State
```

### CanonicalEntry

```text
Id
RelativePath
Size
ExpectedHash
SourceFileEntryId
```

### Plan

```text
Id
CreatedUtc
CanonicalRootId
Status
EstimatedBytesCopied
```

### PlanOperation

```text
Id
PlanId
Sequence
Type
SourceRootId
SourcePath
DestinationRootId
DestinationPath
ExpectedSize
ExpectedHash
Status
StartedUtc
CompletedUtc
Error
```

### ExecutionLog

```text
Id
PlanOperationId
TimestampUtc
Level
Message
```

EF Core creates the schema for a new database. Before changing the schema of databases that must retain data, introduce migrations and replace direct schema creation.

---

## 26. CLI

Suggested commands.

### Initialize

```bash
backup-normalizer init
```

Creates database/configuration.

### Add root

```bash
backup-normalizer root add nas /data --role canonical
backup-normalizer root add d1 /mnt/d1 --role backup
```

### List roots

```bash
backup-normalizer root list
```

### Scan

```bash
backup-normalizer scan nas
backup-normalizer scan d1
backup-normalizer scan --all
```

### Hash

Hash only ambiguous/unresolved candidates:

```bash
backup-normalizer hash --needed
```

Force hashing:

```bash
backup-normalizer hash nas --all
```

### Plan

```bash
backup-normalizer plan --canonical nas
```

### Show plan

```bash
backup-normalizer plan show <plan-id>
```

### Export plan

```bash
backup-normalizer plan export <plan-id> --format json
```

### Execute

```bash
backup-normalizer execute <plan-id>
```

### Resume

```bash
backup-normalizer execute <plan-id> --resume
```

### Verify

```bash
backup-normalizer verify <plan-id>
```

### Purge trash

```bash
backup-normalizer purge --older-than 30d
```

Must require confirmation unless `--yes` is supplied.

---

## 27. Distributed scanning

The architecture should allow a scanner to run near the physical disk.

Example:

```text
QNAP
  └── scans N locally

Desktop
  ├── scans D1 locally
  ├── scans D2 locally
  └── runs global planner
```

The first implementation may use an exported SQLite database or manifest.

Future option:

```text
backup-normalizer inventory export nas --output nas.inventory
backup-normalizer inventory import nas.inventory
```

The exported inventory should contain metadata and hashes, not file contents.

This prevents hashing NAS files over SMB merely to compare them.

---

## 28. QNAP deployment

Create a dedicated Docker target.

Suggested Dockerfile direction:

```dockerfile
FROM alpine:3.17

RUN apk add --no-cache \
    libgcc \
    libstdc++

WORKDIR /app

COPY publish/ .

ENTRYPOINT ["./BackupNormalizer"]
```

Publish:

```bash
dotnet publish \
    -c Release \
    -r linux-musl-arm \
    --self-contained true
```

The build pipeline must include a compatibility test for the QNAP 32 KB page-size environment.

If required, native components must be built with suitable ELF maximum page-size alignment.

The scanner should support a read-only container mount.

---

## 29. Concurrency

Scanning and hashing should support controlled concurrency.

Defaults should be conservative.

For HDD/NAS storage:

- directory enumeration may be concurrent
- hashing should typically use low concurrency
- avoid causing random-seek workloads across many large files

Example:

```bash
backup-normalizer hash --needed --parallelism 2
```

Do not assume maximum CPU concurrency gives maximum disk throughput.

---

## 30. Large files

The application must support files larger than 4 GB.

Use 64-bit file sizes everywhere.

Do not load entire files into memory.

Hash and copy using streaming buffers.

Suggested copy/hash buffer:

```text
1-8 MiB
```

Make buffer configurable if useful.

---

## 31. Special filesystem considerations

The implementation should handle or explicitly report:

- case-insensitive filesystems
- invalid destination characters
- Windows reserved file names
- path length limits
- alternate data streams
- sparse files
- permissions
- ownership
- extended attributes
- symbolic links

Version 1 does not need to preserve every metadata feature.

The minimum required preserved metadata is:

- file bytes
- relative path
- modified timestamp where practical

Any metadata not preserved should be documented.

---

## 32. Safety invariants

The following rules are mandatory.

### Invariant 1

Never permanently delete the last known valid copy of a content group.

### Invariant 2

A file must never be considered identical based only on file name.

### Invariant 3

A file must never be deleted based only on size.

### Invariant 4

Permanent delete is separate from normalization.

### Invariant 5

Every copied file must be verified before the source copy may be trashed.

### Invariant 6

Execution must not silently overwrite different destination content.

### Invariant 7

A scan/planning command must not modify user files.

### Invariant 8

A stale plan must fail safely rather than attempting to adapt destructively.

---

## 33. Conflict handling

Examples of conflicts:

- destination exists with different hash
- source disappeared
- source changed since plan generation
- previously identical duplicate changed
- insufficient free space
- destination filesystem is read-only
- case collision

Conflicts must stop the affected operation.

Default behavior should be:

```text
fail operation
continue unrelated safe operations
report summary
```

Support an optional `--stop-on-error`.

Never resolve conflicting content automatically by overwriting it.

---

## 34. Reporting

Plan summary should show at minimum:

- scanned files
- known content groups
- files needing hashes
- files already correct
- moves
- copies
- trash operations
- conflicts
- estimated bytes copied
- bytes avoided by same-filesystem moves
- estimated resulting disk usage

Example:

```text
Canonical files:                1,421,994
Already correct:                1,310,522
Same-filesystem moves:             92,118
Copies required:                    18,441
Duplicate files to trash:            9,407
Conflicts:                               11

Actual data to copy:               186.4 GB
Data avoided through moves:          2.7 TB
```

---

## 35. Logging

Use structured logging.

Every filesystem-changing operation must be journaled.

Recommended fields:

```text
timestamp
planId
operationId
type
source
destination
size
hash
result
error
```

Logs should be human-readable and optionally JSON.

---

## 36. Configuration

Suggested configuration file:

```text
backup-normalizer.json
```

Example:

```json
{
  "database": "./backup-normalizer.db",
  "hashAlgorithm": "blake3",
  "hashParallelism": 2,
  "copyParallelism": 1,
  "trashDirectoryName": ".backup-normalizer-trash",
  "roots": []
}
```

Environment variables should be usable in Docker.

---

## 37. Implementation phases

### Phase 1 — Inventory

Implement:

- project structure
- SQLite schema
- root management
- recursive scan
- incremental scan
- metadata persistence
- BLAKE3 hashing
- hash caching

No filesystem modifications.

### Phase 2 — Comparison and planner

Implement:

- canonical tree import
- content grouping
- ambiguity detection
- demand-driven hashing
- MOVE planning
- COPY planning
- duplicate/TRASH planning
- byte-cost reporting
- JSON plan export

Still no filesystem modifications.

### Phase 3 — Safe executor

Implement:

- MKDIR
- MOVE
- COPY-to-temp
- verification
- TRASH
- journaling
- resume
- conflict handling

### Phase 4 — Purge and polish

Implement:

- explicit permanent purge
- reporting
- Docker images
- QNAP ARM32 build/test
- inventory export/import

---

## 38. Testing strategy

### Unit tests

Test:

- path normalization
- hash cache reuse
- same-size different-content handling
- content grouping
- move-vs-copy planning
- duplicate handling
- source-selection cost
- stale plan detection

### Integration tests

Create temporary filesystems with trees such as:

```text
Canonical:
  Photos/a.jpg
  Docs/a.txt

Disk:
  Old/a.jpg
  random/a.txt
```

Test:

- correct move detection
- zero-byte transfer where possible
- copy when necessary
- no false deletes
- resume after forced interruption

### Destructive safety tests

Explicitly test:

1. source disappears after planning
2. source content changes after planning
3. destination suddenly exists
4. destination contains different bytes
5. disk fills during copy
6. process crashes during copy
7. process crashes after move but before DB update
8. duplicate designated for trash becomes the only surviving copy

All must fail safely.

---

## 39. QNAP compatibility acceptance test

Before relying on the NAS deployment, verify:

```bash
uname -m
getconf PAGESIZE
```

Expected approximately:

```text
armv7l
32768
```

Then verify container/app:

```bash
BackupNormalizer --version
BackupNormalizer db-test
BackupNormalizer scan-test /data
```

`db-test` should:

1. create SQLite database
2. create table
3. insert rows
4. read rows
5. transaction commit
6. transaction rollback
7. delete temporary DB

The scanner is considered supported on QNAP only after this test passes on the actual NAS.

---

## 40. Non-goals for version 1

Do not implement initially:

- GUI
- cloud service
- deduplicating filesystem
- block-level delta transfers
- compression
- RAID management
- automatic backup scheduling
- filesystem snapshots
- remote daemon protocol
- content-aware merge of conflicting files
- automatic overwrite conflict resolution

Keep version 1 focused on safe file-tree normalization.

---

## 41. Future enhancements

Potential later additions:

- GUI planner
- web UI
- filesystem watching
- scheduled scans
- network-aware copy-cost model
- reflink detection
- hard-link deduplication
- block-level copy for huge partially changed files
- duplicate browser
- canonical path rules
- multiple desired replicas
- parity/redundancy policies
- REST API
- remote NAS scanner service

---

## 42. Definition of done for MVP

The MVP is complete when it can:

1. Scan `N`, `D1`, and `D2`.
2. Persist inventories in SQLite.
3. Reuse hashes across unchanged scans.
4. Identify identical content independent of path/file name.
5. Accept one tree as the canonical layout.
6. Generate a deterministic normalization plan.
7. Prefer same-filesystem moves over copies.
8. Report how many bytes would actually be transferred.
9. Export the plan without touching user files.
10. Execute an approved plan.
11. Verify copied content.
12. Move deleted duplicates to recoverable trash.
13. Resume after interruption.
14. Refuse unsafe/stale operations.
15. Run on the QNAP ARM32/32K environment using the dedicated container build.

---

## 43. Guiding principle

When several possible transformations produce the same canonical result, choose the one that:

1. preserves data safety,
2. moves existing content locally instead of copying it,
3. copies the fewest bytes,
4. performs the fewest destructive operations,
5. remains fully explainable in the generated plan.

The tool should always be able to answer:

> Why is this operation necessary, and what verified copy of the data remains afterward?
