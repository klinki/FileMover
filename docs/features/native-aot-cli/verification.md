# Native AOT CLI verification

## State

First EF Core trial completed, 2026-10-05. All 14 candidate static-query checks passed on the actual NAS. EF Core remains a viable approach for further conversion. Production inventory collection and migration deployment are not implemented yet.

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

## Remaining work

Continue with EF and convert the inventory query paths using the demonstrated static shapes. Broader conversion, custom collation, old file-metadata projections, complete inventory status, exclusions, and parameter combinations remain unverified. Implement the AOT migration helper and full scan/hash/export job separately. Full CLI compatibility and the existing NAS compatibility bug remain open. Production source was unchanged.

Python syntax, shell syntax, C# formatting, and documentation links were checked. The final candidate source passed its Windows 14-check baseline after the query-shape changes. The full application test suite was not rerun because this trial adds isolated tooling and leaves production source unchanged.
