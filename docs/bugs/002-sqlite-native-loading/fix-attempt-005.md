# Fix attempt 005

## Attempt status

EF Core Native AOT feasibility trial completed, 2026-10-05. Candidate static queries passed on the NAS. No full application repair is claimed.

## Goal and relation to prior attempts

[Attempt 002](fix-attempt-002.md) demonstrated working AOT runtime and SQLite probes but found that the full application's EF model could not build at runtime. [Attempt 004](fix-attempt-004.md) also found that official .NET 6 hello-world builds fail on this NAS. The user selected AOT, approved an EF trial first, and requested a first implementation.

## Proposed change

Create a standalone trial using the production database context/entities and a source snapshot of the current facade. Prepare synthetic current and legacy schemas using the ordinary build. Generate a compiled model and query interceptors during AOT publication, then test reads, writes, deletion, and transaction behavior on the actual NAS. Preserve failed generation logs. Scope static query alternatives separately if unchanged queries cannot precompile.

## Risks and verification

EF AOT tooling is experimental. Passing static query samples does not prove the unchanged application's dynamic queries work. Production migration startup is outside this trial; only the snapshot bypasses that call with prepared fixtures. Validate ordinary-build baselines, ARM ELF alignment, NAS results, transferred hashes, and original services. Do not mount or modify existing NAS data.

## Implementation and results

The [feature verification](../../features/native-aot-cli/verification.md) records source files and results. Both 14-check Windows baselines pass. Full query generation fails on current dynamic/composed queries; the failure log is preserved. Explicit EF generation on managed Linux assemblies produced the core model and candidate query interceptors. Local context variables and the expression setter overload resolved the observed candidate-query generation failures.

The static suite passed all 14 checks on the actual NAS under Native AOT 10.0.10 with EF 10.0.12, including reads, inserts, updates, deletion, hash persistence, commit/rollback, aggregation, and a legacy read-only query. The facade suite opened the database with the compiled model but failed its other 13 checks because its queries were intentionally not precompiled in this bounded build. Native alignment and all transferred hashes passed, and all seven original services remained running. No NAS share was mounted or modified.

## Outcome

EF Core is viable for the next inventory query conversion step on this NAS. The unchanged facade and production migrations are not AOT-compatible yet. The compatibility bug remains open. No production migration or inventory job has been run, and production application source remains unchanged.
