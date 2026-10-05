# Native AOT CLI implementation plan

## State

Plan agreed, 2026-10-05. The user confirmed NAS inventory collection as the first release, selected an EF Core AOT trial before considering a database rewrite, and accepted the separate AOT migration helper. The trial passed 14 checks. The actual application now passes the synthetic inventory acceptance job on the NAS, including creation, upgrade, scan, hash, and export, in the clean AOT runtime image. [Verification](verification.md) records both milestones. Clean-machine toolchain setup, release hardening, and later CLI qualification remain open.

Canonical path: [implementation-plan.md](implementation-plan.md).

## Goal

Deliver a reproducible Native AOT command-line application that works on the actual ARMv7 QNAP TS-431P3 with 32,768-byte pages. Preserve the inventory database format, JSON contracts, and compatibility with the Windows application.

The first release collects inventories on the NAS: database creation and upgrades, root registration, directory scanning, exclusions, hashing and cache reuse, inventory diagnostics, and portable database export. Comparison, duplicates, planning, and replay follow as a second acceptance milestone.

## Evidence and remaining work

- [Native AOT investigation](../../bugs/002-sqlite-native-loading/fix-attempt-002.md) confirms managed startup, SHA256, file read/write/move operations, and an independent SQLite create/insert/read/rollback check on this NAS.
- The experimental full CLI passes version/help but database and scan commands fail because EF needs a compiled model.
- [JSON serialization](../aot-json/verification.md) is implemented and passed an on-device AOT probe. Include it in the full application checks.
- [.NET 6 testing](../../bugs/002-sqlite-native-loading/fix-attempt-004.md) also failed under Ubuntu and Alpine. A CoreCLR-based migration helper has no demonstrated working runtime on this NAS.
- The application targets .NET 10 with EF Core SQLite 10.0.12. The working AOT experiment used SDK 10.0.302, native runtime 10.0.10, Clang/LLD 18.1.3, and an Alpine 3.17 ARMv7 sysroot. That experiment is a baseline, not a final release toolchain selection.
- [Database construction](../../../src/BackupNormalizer.Core/Database.cs) currently calls runtime EF migrations and builds queries using shared expressions and conditional composition. [Inventory queries](../../../src/BackupNormalizer.Core/Database.Inventory.cs) also branch for older read-only schemas.

Microsoft's [EF Native AOT documentation](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries) describes experimental support, requires query precompilation, and lists dynamic composition and LINQ query syntax as limitations. A compiled model alone does not resolve query execution.

## Agreed decisions

Keep .NET 10 for the initial work. Use the already demonstrated `linux-musl-arm` AOT target and Alpine 3.17 compatibility baseline. Evaluate SDK/runtime patch updates on the actual NAS before freezing release versions; neither a framework upgrade nor static ELF inspection establishes compatibility.

The user selected an EF Core trial first. Try EF Core in one representative AOT workflow before changing the complete database implementation. If EF cannot execute the required operations reliably, propose moving database access behind the existing `Database` facade to direct `Microsoft.Data.Sqlite` commands with explicit row mapping. A broad rewrite requires a separate decision based on the trial results. Keep one implementation of each database operation shared by desktop and NAS where practical. Avoid a permanent collection of NAS-only replacements for arbitrary EF queries.

Keep the user's separate migration binary design. The helper is a small AOT executable applying SQL generated during the build from the existing EF migrations. EF remains the schema authoring tool on the build machine. The NAS does not need the SDK or EF tooling.

The helper recommendation follows the runtime test evidence. An ordinary self-contained EF migration bundle still depends on a working managed runtime; it should not be assumed to solve this NAS problem. [EF migration deployment documentation](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) supports build-generated scripts and explains that SQLite cannot generate generic idempotent migration scripts.

## Stage 1: prove the database approach

Use an isolated branch or source snapshot to generate the model and query interceptors with EF tooling matched to the project's EF version. Generate interceptors during the publish workflow because source edits invalidate them.

Run a small executable using the actual application database code against a current-schema temporary database. Prepare that database on the build machine so this trial does not depend on the unfinished migration helper.

Exercise root insert/update/read, file insert/update/read, hash persistence and retrieval, an inventory query, and transaction commit/rollback. Include `SaveChanges`, `ExecuteUpdate`, and `ExecuteDelete` where the inventory workflow uses them. Verify preservation of parameterization and SQL-side filtering. Include one older read-only database projection to test the schema compatibility path.

Record the exact unsupported queries and build warnings. Conditional queries must become complete, statically discoverable queries. Shared expression projections and LINQ query syntax require review. Do not implement `EF.CompileQuery` as a substitute for publish-time AOT query generation or materialize entire tables just to bypass translation.

Proceed with EF only after this workflow passes on the NAS and the remaining required queries have a credible conversion path. If it fails, present the concrete blockers and the extent of the direct SQLite alternative before starting a broad rewrite. No production EF compatibility claim follows from a successful publish or model generation.

Deliverable: a short verification record and a settled database access decision.

First trial result: keep EF for the next conversion step. The production model and statically precompiled query samples work on the NAS, including updates and transactions. Explicit managed Linux generation avoids the observed publish-time integration problems. Use local context variables and the expression setter overload for numeric updates. Complete inventory query conversion still needs verification; the unchanged facade cannot execute its queries in the bounded AOT build.

## Stage 2: make schema upgrades independent of runtime EF

Build a migration manifest and ordered forward SQL from the six existing migrations. Generate known-state transitions rather than an SQLite idempotent script. Keep migration IDs and `__EFMigrationsHistory` compatible with existing databases.

Before creating an EF context or opening a writable application database, inspect migration history using SQLite. Create a new database through the helper when necessary. Launch the helper only when an upgrade is required, using its fixed path beside the CLI. Require a matching application/helper release and propagate failure before any inventory writes.

The helper must recheck the schema after obtaining migration access, serialize competing migration attempts, and fail clearly when another writer prevents safe migration. Use SQLite transaction and lock behavior appropriate to the generated scripts. Account for table rebuilds, foreign-key PRAGMAs, and transaction boundaries explicitly; do not blindly wrap every generated script in another transaction.

Create a consistent SQLite backup before upgrading an existing database. Reuse the established backup/export mechanism where possible so a WAL database is not copied incompletely. Preserve the backup on failure. Check final migration history, integrity, and foreign keys before reporting success. Restarting the helper should apply only unfinished transitions supported by the recorded state.

Read-only opens never migrate. Unknown or newer schema states fail writable startup without attempting a downgrade. Preserve existing supported older-schema reads; unrecognized legacy databases require an explicit supported upgrade path.

Test empty database creation, upgrades from every known migration, already-current startup, newer/unknown states, mismatched or missing helper, concurrent attempts, failure recovery, and read-only behavior. Verify upgrades on temporary copies only.

Deliverable: matched CLI and AOT migration helper with tested schema transitions.

## Stage 3: complete the selected CLI workflows

Convert and verify every database operation used by the first-release scope. Keep exclusions, hash cache invalidation, scan error recording, symlink behavior, path case handling, and database export semantics consistent with the current application.

Run the same compatibility tests against the ordinary Windows build and the AOT database implementation. Preserve the existing schema, public `Database` API where practical, and JSON output contracts. Add focused checks for actual compatibility gaps rather than duplicating the implementation in tests.

Perform an end-to-end NAS job on generated fixtures: create a database, register a root, scan, hash, query diagnostics, export, and open the exported database in the Windows application. Hash a second time to verify cache reuse. Change, move, and remove fixture files, then rescan and verify the resulting inventory. Include Unicode names, excluded paths, links, unreadable entries where reproducible, and cancellation/restart behavior.

If commands remain outside the first release, the release must make that limitation explicit and reject unsupported commands before changing files or database state. Do not publish an apparently complete CLI with known unverified execution paths.

Deliverable: usable inventory collection on synthetic NAS data and compatible exported inventories.

## Stage 4: make builds and deployment reproducible

Move the proven build steps out of ignored experiment folders into maintained deployment tooling. Build through Linux, using Docker or WSL on the Windows PC with a pinned cross-compiler and ARM sysroot. [Native AOT cross-compilation documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile) describes the target native toolchain requirements; the current Windows Docker cross-publish command needs additional AOT build support.

Update the [QNAP Dockerfile](../../../Dockerfile.qnap) and [build helper](../../../deploy/build-qnap.ps1) to package the AOT CLI, AOT migration helper, and matching migration data. Preserve ELF load alignment compatible with 32 KiB pages, using the tested 64 KiB alignment baseline. Verify every shipped native executable and library, then run them on the actual NAS.

Build-generated models, query interceptors, and migration data must match the exact sources being published. Record SDK, EF, runtime, compiler, sysroot, base image digest, image size, and executable checksums. Resolve or explicitly justify warnings relevant to the accepted command paths. A successful publish is insufficient acceptance.

The runtime image needs no CoreCLR, SDK, or compiler. Keep read-only inventory data mounts and a separate writable state directory. Include globalization and Unicode acceptance checks; the experimental invariant-globalization setting needs an explicit decision and verification for release.

Update the [NAS check](../../../qnap-check.sh), [inventory job](../../../qnap-inventory.sh), deployment instructions, and verification record. Continue using new isolated containers and generated data during development. Testing existing NAS shares and upgrading existing inventories requires the user to choose the test paths and database copies.

Deliverable: reproducible image and import bundle that pass the complete first-release acceptance job on the QNAP.

## Later acceptance milestone

After the inventory release, qualify comparison, file differences, duplicate groups, plan import/export, and replay separately. Exercise copy and move operations only on generated files, including conflict checks, verification, failure logs, and interrupted execution. Do not run the existing D replay report during AOT qualification; its real source data resides on another computer.

The Windows GUI continues to use the ordinary build. Native AOT publishing for the GUI and .NET 11 adoption are separate decisions.

## Confirmed choices

- First release: NAS inventory collection, confirmed by the user.
- Database approach: trial EF compiled models and precompiled queries first, confirmed by the user.
- Migration approach: separate AOT helper applying build-generated EF migration SQL, accepted by the user.
- First implementation: stage 1 covered representative reads, writes, and transactions on synthetic data, authorized by the user's request for a first version.
- Application conversion started, 2026-10-05, following the user's request to proceed to a running application. Implement the agreed inventory scope, shared static EF queries, an AOT migration helper, and an on-device scan/hash/export acceptance job. Use `NATIVE_AOT` only for startup and first-release command availability. No additional product decision is needed for this scope.
- Application acceptance passed, 2026-10-05. Shared queries, the generated SQL helper, and inventory commands work on synthetic NAS data. The portable NAS export opens through Windows application database code. The clean runtime-image build still needs verification with a running PC Docker engine.

The EF trial determines whether the current database implementation can be retained. A direct SQLite rewrite remains a fallback requiring a decision based on the trial results.

## Planning verification

The plan follows the actual NAS results and current database call sites. The original planning step changed only documentation. Subsequent stage 1 implementation and on-device results are recorded in [verification](verification.md), with the original runner output and test logs preserved in Git.
