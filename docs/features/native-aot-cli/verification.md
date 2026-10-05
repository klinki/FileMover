# Native AOT CLI verification

## State

Application inventory acceptance passed, 2026-10-05. The actual AOT CLI and separate migration helper run on the 32 KiB-page NAS using synthetic data. Earlier sections preserve the first EF trial; the application results below record the subsequent conversion. Comparison and replay remain outside the qualified NAS command set.

## Implemented trial

The [trial project](../../../tools/BackupNormalizer.AotTrial/BackupNormalizer.AotTrial.csproj) references the actual core project. Its [program](../../../tools/BackupNormalizer.AotTrial/Program.cs) prepares current and initial-migration databases and checks the existing `Database` facade. [Static query checks](../../../tools/BackupNormalizer.AotTrial/StaticTrial.cs) exercise candidate AOT query shapes against the production context and entities, including hash persistence and updates.

The [snapshot preparation](../../../tools/BackupNormalizer.AotTrial/prepare-snapshot.py) preserves source hashes and rejects existing output directories. [AOT setup](../../../tools/BackupNormalizer.AotTrial/enable-aot.py) changes only the snapshot, configures EF generation, and bypasses the constructor's migration call because the experiment supplies already-migrated fixtures. This bypass is not a production migration solution. [Trial instructions](../../../tools/BackupNormalizer.AotTrial/README.md) document preparation, publication, and NAS execution.

The [Linux build script](../../../deploy/build-ef-aot-trial.sh) uses the existing isolated ARM toolchain. The [NAS runner](../../../tools/BackupNormalizer.AotTrial/test-nas.py) creates isolated containers, checks ARM ELF page alignment and transferred hashes, preserves original services, and never mounts existing NAS shares.

## Managed baselines

Both suites passed on Windows using SDK 10.0.302, runtime 10.0.12, and EF Core 10.0.12:

- Existing database facade: 14 checks passed, including root/file reads and writes, checkpoint deletion, commit/rollback, inventory status, and an initial-schema read-only root query.
- Candidate static queries: 14 checks passed, including root/file reads and writes, hash insert/read/update, checkpoint deletion, commit/rollback, an inventory count, and an initial-schema read-only root query.

Every check uses a separate temporary copy of synthetic data. SQL checks independently verify the write results.

## Full query-generation experiment

The [initial build log](../../../publish/ef-aot-trial-20261005/build.log) records a failed publish with EF model and query generation enabled for the unchanged database methods. The model generated, but query precompilation rejected conditional composition, shared expression projections, and queries over intermediate `IQueryable` values. It reported `Dynamic LINQ queries are not supported when precompiling queries`, plus translation errors for composed subqueries.

The failure is preserved. The smaller experiment generates the compiled model in the copied core project and precompiles candidate static queries in the trial project. Core facade query precompilation is disabled only for this bounded experiment. Success establishes candidate query shapes, not compatibility of the unchanged facade or the complete inventory workflow.

## Generation adjustments

The successful build uses explicit EF CLI generation on managed Linux assemblies without an ARM runtime identifier, followed by ARM AOT publication of those generated sources. The prior [MSBuild experiment logs](../../../publish/ef-aot-trial-static-20261005/build-002.log) record intermediate apphost/assembly-loading problems. Those experimental integration failures do not establish a runtime limitation.

The [candidate-query generation log](../../../publish/ef-aot-trial-manual-20261005/build.log) shows that direct method-parameter context roots were rejected. Introducing a local context variable allowed the candidate queries to proceed. The [numeric update failure](../../../publish/ef-aot-trial-manual-20261005/build-003.log) records a generator `NullReferenceException` for the value setter overload. The expression setter `SetProperty(x => x.Size, x => updatedSize)` generated and executed successfully. Retain these tested shapes while converting the production queries.

The [successful build log](../../../publish/ef-aot-trial-manual-20261005/build-004.log) records SDK 10.0.302 publication with EF tooling/packages 10.0.12 and ARM native runtime 10.0.10. The core model generated 23 C# files; candidate query interceptors generated approximately 45 KiB of source. Trimming/AOT warnings remain, including the intentionally unconverted facade. They require further review before release.

## NAS results

The committed [on-device result](evidence/nas-result.json) confirms ARM, 32,768-byte pages, .NET 10.0.10, and dynamic code disabled. This is the original runner output from 2026-10-05, preserved with the test logs so the evidence remains available after cloning. Larger build artifacts and generation logs remain local under `publish/`.

- Candidate static-query suite: exit 0, all 14 checks passed. Root/file reads and writes, hash persistence and update, checkpoint deletion, commit/rollback, inventory aggregation, and a legacy read-only root query work with the production model and entities.
- Existing facade suite: exit 1, database opening passed and 13 query-backed checks failed with `Query wasn't precompiled and dynamic code isn't supported with NativeAOT.` This is expected in the bounded build, which excludes those facade queries from precompilation. It confirms that a compiled model alone is insufficient.
- Both native binaries have 65,536-byte ELF load-segment alignment and offsets congruent at 32 KiB boundaries. Executable, SQLite library, and both fixture database hashes matched after transfer.
- All seven original services remained running. Test containers had no share mounts, disabled networking, a read-only root, and writable temporary storage. No existing NAS data was accessed.

The retained image is `backup-normalizer:ef-aot-trial-20261005-171638`. Its default command runs the static suite against packaged fixtures. This diagnostic image is not an inventory job image. The unstripped executable is 81,759,464 bytes and retains debug symbols; no release size optimization was attempted.

Captured logs:

- [Passing static suite](evidence/static-nas.log)
- [Unconverted facade suite](evidence/facade-nas.log)
- [Source verification](evidence/source-verification.json), recording 47 unchanged core source files and matching tested trial sources
- [Tested source snapshot](../../../publish/ef-aot-trial-manual-20261005/source/tools/BackupNormalizer.AotTrial/StaticTrial.cs)

## Remaining work after the first trial

Continue with EF and convert the inventory query paths using the demonstrated static shapes. Broader conversion, custom collation, old file-metadata projections, complete inventory status, exclusions, and parameter combinations remain unverified. Implement the AOT migration helper and full scan/hash/export job separately. Full CLI compatibility and the existing NAS compatibility bug remain open. Production source was unchanged.

Python syntax, shell syntax, C# formatting, and documentation links were checked. The final candidate source passed its Windows 14-check baseline after the query-shape changes. The full application test suite was not rerun because this trial adds isolated tooling and leaves production source unchanged.

## Actual application implementation

The [application instructions](README.md) describe the maintained build. Shared database queries now use complete expressions, local scalar/context captures, and expression setters. Optional filters select complete query branches outside LINQ expressions. Legacy projections remain separate so historical read-only databases do not require newer columns. Windows uses these same query shapes.

[AOT build properties](../../../src/BackupNormalizer.Core/NativeAot.props) define `NATIVE_AOT` during managed generation and native publication. Conditional compilation controls startup migrations, the build-machine SQL generator, and the qualified CLI command set. Inventory logic remains shared. The [schema library](../../../src/BackupNormalizer.Schema/DatabaseSchema.cs) checks history before writable EF construction and invokes the matching helper. [SQL generation](../../../src/BackupNormalizer.Core/MigrationManifestGenerator.cs) uses existing EF migrations; the [generated package](evidence/inventory-migrations.json) is preserved with this build's evidence.

The helper checks the package hash, retains a consistent backup for older inventories, serializes helper attempts, rechecks history under SQLite write locks, and commits each migration with its history row. Unknown, newer, or incomplete histories fail. Transaction-suppressed commands fail generation. Read-only startup never migrates. Root listing opens read-only in both builds, and scanning excludes the active database's migration lock.

## Application generation findings

The generator rejected record-property captures, array membership, unnamed anonymous join members, and implicit nullable numeric setter conversions. Scalar locals, `List.Contains`, explicit member names, and casts resolved those failures. Optional filters needed complete query branches to preserve runtime expression bindings.

The first application NAS run exposed reversed extraction of chained setter values, corrupting the synthetic root path. The [failed result](../../../publish/inventory-aot-20261005-v5/nas-result.json) remains local. The pinned EF 10.0.12 [generation corrections](../../../deploy/fix-ef-interceptors.py) align runtime extraction positions with generated setter order and add a `Database` type alias. Fifty parameter bindings receive the correction; single-setter positions are unchanged. Review this workaround when changing EF versions.

The [precompiled managed acceptance log](evidence/inventory-precompiled-managed.log) verifies generated-query execution before ARM publication. It covers separate roots, root-specific filtering and hashing, scans, selected-scan diagnostics, updates, cache reuse, a database inside its scanned root, and export. It independently checks stored paths, sizes, timestamps, and digests.

## Application verification

- Windows compatibility suite: 481 passed, 20 skipped. Twelve new migration cases cover all seven known starting states, preservation, rollback/retry, history rejection, package mismatch, helper locking, and read-only root listing.
- After the final optional-filter and lock-exclusion changes, 54 targeted inventory, hashing, export, exclusion, migration, and JSON checks passed.
- Native publication completed with SDK 10.0.302, EF 10.0.12, and ARM runtime 10.0.10. EF/provider trimming and AOT warnings remain, together with SpatiaLite-loading warnings. This workflow uses no spatial extensions. Experimental warnings still need release review.
- [NAS application result](evidence/inventory-nas-result.json): exit 0, ARMv7, 32,768-byte pages. The [console log](evidence/inventory-nas.log) records creation/transactions, root insertion/update/listing, scans, stored exclusions, links, diagnostics, two-worker hashing, reuse, changed/moved/removed-file rescanning, status, export, and a backed-up legacy upgrade.
- Mismatched migration packages and unqualified replay commands failed before creating the requested databases.
- Independent SQLite checks verified integrity, foreign keys, six migration rows, correct root values, missing/moved paths, Unicode filenames, exclusions, sizes, hash metadata, and exact SHA256 digests. The NAS export opened through the ordinary Windows CLI and reported four usable files, one link, two missing entries, and no entry errors.
- All transferred hashes matched. CLI, helper, and SQLite have 65,536-byte load alignment. No existing share was mounted, no container was OOM-killed, and all seven original services stayed running.
- [Source verification](evidence/inventory-source-verification.json): all 60 maintained source files match the tested snapshot. Models and interceptors remain build artifacts.

The first working NAS image is `backup-normalizer:inventory-aot-20261005-182026`. Its entrypoint is the actual application; help lists qualified inventory commands. This diagnostic image uses the earlier Alpine test base, which retains unused CoreCLR files. Executables, the export, snapshots, and full build logs remain under local `publish/inventory-aot-20261005-v9` output.

## Clean runtime image verification

The maintained Dockerfile subsequently built directly through the NAS Docker engine. The PC Docker engine was stopped, and Docker CLI stdin staging failed because the NAS's 64 MiB `/tmp` was full. Streaming the context to the daemon's Unix socket over SSH succeeded without clearing files. The [build log](evidence/inventory-runtime-image-build.log) and [result](evidence/inventory-runtime-image-result.json) record the clean image `backup-normalizer:inventory-aot-runtime-20261005-183056`, ID `sha256:31f2de7c17faf0d5fd191717af54d9767c444496bc7b62076b083bbe2ebfbd42`. Docker reports ARM and 208,600,488 bytes uncompressed. Help ran successfully and checks confirmed that `/app/libcoreclr.so` and `/app/System.Private.CoreLib.dll` are absent. Native executables retain debug symbols; size optimization remains open.

The full synthetic inventory acceptance job passed again using this clean image as its base: [result](evidence/inventory-clean-nas-result.json), [log](evidence/inventory-clean-nas.log). Its retained acceptance image is `backup-normalizer:inventory-aot-20261005-183216`. Export contents and transfer hashes passed independent checks, no existing share was mounted, and all seven original services remained running. The fixture layer is used only by the acceptance job; the clean runtime image contains the application and job scripts.

The packaged `qnap-inventory.sh` also completed independently against the six internal fixture files: [job log](evidence/inventory-job-nas.log). Platform checks, database creation, registration, scan, hashing, and final status passed with a read-only container root and temporary state. The clean-image export opened through the ordinary Windows CLI: [status](evidence/inventory-clean-export-windows.log). Existing NAS shares and state remained unmounted.

## Remaining release work

Provide clean-machine toolchain setup, verify archive import through Container Station, review experimental warnings and binary size, and qualify cancellation/restart and additional parameter combinations on the NAS. Existing NAS inventories and shares have not been tested or upgraded. Comparison, duplicates, planning, and replay remain a separate milestone. Verification used an uncommitted source snapshot; no image was pushed to a registry.
