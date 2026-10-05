# Fix attempt 002

## Attempt status

Native AOT feasibility verified on the actual QNAP. Full CLI functionality remains blocked by its current EF Core integration.

## Goal

Build and run a minimal ARM32 Native AOT probe on the actual 32 KiB-page QNAP. If it passes, assess the full CLI's AOT compatibility.

## Relation to previous attempts

[Attempt 001](fix-attempt-001.md) repaired Windows SQLite loading. Actual NAS testing subsequently found a CoreCLR startup crash, including in a probe without SQLite or EF Core.

## Proposed change

Prepare an isolated Linux SDK, cross-linker, and ARM target libraries under the workspace. Publish a standalone probe using Native AOT and ensure its ELF load segments are compatible with 32,768-byte pages. Run it in a new test container on the NAS. Keep application source changes separate from this feasibility experiment.

## Risks

Native AOT still includes native runtime code and platform libraries. Avoiding the JIT does not prove that it can run on the old QNAP kernel. The full application's EF Core model, queries, migration startup, and JSON serialization may need changes.

## Files and components

The [Native AOT probe source](../../../publish/nas-test-20261005/aot-probe-source/Program.cs), [SQLite probe source](../../../publish/nas-test-20261005/aot-sqlite-source/Program.cs), temporary Linux build tools, and isolated NAS test containers. Build artifacts are retained under the workspace's ignored publish directory; these artifact links are local to this checkout.

## Verification plan

- Record compiler and runtime versions and the exact target RID.
- Inspect the executable architecture and load alignment.
- Verify managed entry, runtime description, actual page size, and a SHA256 computation on the NAS.
- Confirm existing NAS services remain running and no existing NAS share is mounted.
- Attempt full CLI publication only after the probe establishes a viable runtime path.

## Implementation summary

Prepared Linux x64 SDK 10.0.302 inside the workspace, with Clang/LLD 18.1.3 and an Alpine 3.17 ARMv7 musl sysroot. Used WSL to publish `linux-musl-arm` Native AOT executables with runtime 10.0.10 and 65,536-byte ELF load alignment. Tool packages were extracted into the workspace without installing system packages or changing host configuration.

Published a standalone runtime/file-operation probe, the full CLI from an isolated source snapshot, and a separate SQLite probe using Microsoft.Data.Sqlite 10.0.12. The CLI and core's 49 source/project files still match their recorded snapshot hashes. No production application source was changed for this experiment.

Created separate test images and containers on the NAS. Runs used a read-only container filesystem, writable `/tmp`, no network, and no existing NAS share mounts. The seven original running services remained running. Images and stopped test containers are retained for inspection; no image was pushed.

## Test results

| Check on actual NAS | Result |
| --- | --- |
| Minimal AOT runtime entry | Passed, .NET 10.0.10, ARM, actual page size 32,768, dynamic code disabled |
| SHA256 and temporary file read/write/move | Passed |
| Full CLI Native AOT publication | Passed with trimming and AOT warnings |
| Full CLI ELF inspection | ARM32, all load segments aligned to 65,536 bytes and congruent at 32 KiB boundaries |
| Full CLI `--version` and `--help` | Passed, exit 0 |
| Full CLI `db-test` and `scan-test /app` | Failed, exit 2, EF requires a compiled model |
| Separate AOT SQLite probe | Passed: open/create/insert/read/transaction rollback, SQLite 3.53.3 |

The CLI database and scan commands reported:

```text
Model building is not supported when publishing with NativeAOT. Use a compiled model.
```

This is a managed compatibility error rather than the previous CoreCLR startup crash. A successful publish does not establish that the full CLI is usable.

Captured local evidence:

- [Runtime probe results](../../../publish/nas-test-20261005/native-aot-result.json)
- [Full CLI results and ELF inspection](../../../publish/nas-test-20261005/full-app-aot-result.json)
- [SQLite probe results](../../../publish/nas-test-20261005/sqlite-aot-result.json)
- [Full CLI publish diagnostics](../../../publish/nas-test-20261005/full-app-aot-build.log)

## Outcome

Native AOT can start and perform hashing, file operations, and SQLite work on this NAS. The full application's database-backed commands remain unusable in the tested AOT build. The bug remains open; no full application repair or user acceptance is claimed.

## Remaining gaps

The next implementation needs a compiled EF model and compatible query execution, an AOT-compatible database initialization/migration strategy, and source-generated JSON serialization. Model generation alone should not be treated as a complete repair. [Microsoft's EF Native AOT documentation](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries) describes experimental query precompilation and limitations for dynamically composed queries.

Build warnings identify reflection-based JSON serialization, model construction, and EF dependencies. Apart from the model-construction error, these compatibility gaps have not yet been exercised at runtime. Invariant globalization was used for this experiment; normal globalization behavior is unverified. Full scan/hash/plan/import/replay workflows and original-data replay remain unverified.

## Subsequent JSON implementation, 2026-10-05

The user separately authorized the JSON compatibility changes. Source-generated application JSON passed the Windows suite with reflection disabled and an on-device Native AOT probe using the actual source. See [JSON verification](../../features/aot-json/verification.md) for the implementation and evidence. The earlier results above describe the original unmodified CLI experiment. EF model/query/migration work and full application NAS acceptance remain open.
