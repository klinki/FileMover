# JSON serialization verification

## Result

Implemented and verified on 2026-10-05. Application-owned JSON uses generated metadata in ordinary builds and Native AOT. The CLI, Windows UI, and test host disable default reflection serialization.

The existing database implementation remains unchanged. Full application Native AOT support still depends on EF model, query, and migration compatibility.

## Implementation

- [Core context](../../../src/BackupNormalizer.Core/CoreJsonContext.cs) covers configuration, plans, exclusion policies, location and grouped reports, and structured logging.
- [CLI context](../../../src/BackupNormalizer/CliJsonContext.cs) covers version, configuration, status, coverage, scan diagnostics, and database export output.
- [GUI context](../../../src/BackupNormalizer.Ui/Models/GuiJsonContext.cs) covers session preferences and inventory drag payloads.
- Named output records preserve the fields previously emitted by anonymous objects. Generated contracts preserve computed report properties, null fields, camelCase application output, PascalCase GUI state, compact embedded CSV JSON, and indentation.
- Configuration import remains case insensitive and rejects unknown properties. Plan import remains case insensitive and accepts extra fields. GUI session and drag import retain their original case sensitivity.
- Structured logging accepts explicitly serialized JSON fields or typed fields with generated metadata. Future output types must be registered in their owning context.

## Windows verification

Debug and Release builds passed in an isolated artifact folder, preserving the running GUI's locked binaries. Windows builds used the already installed .NET 11 RC1 SDK and retained the existing `net10.0` target. Existing Avalonia XAML and xUnit analyzer warnings remain; no new serializer diagnostics were reported.

The full Release suite passed with default reflection serialization disabled: 469 passed, 20 skipped, 0 failed. The skipped tests are the existing Windows privilege and headless interaction checks.

[Compatibility tests](../../../tests/BackupNormalizer.Tests/JsonCompatibilityTests.cs) explicitly check the disabled reflection setting, legacy plan casing and extra fields, property names and null fields, Unicode paths, and compact structured logging. Existing tests cover configuration validation, exclusion policies, reports and computed values, CLI output, plan replay, GUI sessions, and inventory drag-and-drop.

The initial targeted run found an incorrectly cased source-kind value in the new test fixture, which was corrected. A GUI drop hit-test assertion failed once, then passed both its focused recheck and the full suite without a production hit-testing change.

Separate CLI processes passed version JSON, configuration creation/show, and JSON error logging for invalid configuration. Configuration creation did not create a database. The generated CLI, UI, and test runtime configuration files all record reflection serialization as disabled.

Local evidence is retained under the ignored publish folder:

- [Full suite results](../../../publish/json-verification/results/full-suite.trx)
- [Focused recheck](../../../publish/json-verification/results/json-recheck.trx)
- [CLI process results](../../../publish/json-verification/cli-json-result.json)

## Actual QNAP Native AOT verification

Built an isolated probe using copies of the actual production core, CLI context, and GUI persistence/context source. All 50 copied files matched their recorded production hashes after publication. Linux SDK 10.0.302 published the probe for `linux-musl-arm` with Native AOT runtime 10.0.10 and 65,536-byte ELF load alignment.

On the actual ARMv7 QNAP, the probe exited 0 and reported a page size of 32,768, dynamic code disabled, and JSON reflection disabled. It passed configuration round trips, casing and validation, plan import/export, location/filename/duplicate JSON and CSV export, all CLI serializer root types, GUI session persistence, inventory drag payloads, and structured logging. GUI persistence and payload serializers ran without starting Avalonia on the NAS.

The publication reported no JSON trimming/AOT warnings. Existing EF model and migration warnings remain in the core dependency and were not exercised by this JSON probe.

Test image: `backup-normalizer:qnap-aot-json-probe-20261005`. Its test entrypoint is `/app/JsonProbe`; the inherited default entrypoint belongs to the older baseline app. The test used a read-only container filesystem, writable temporary storage, no network, and no existing NAS share mounts. The seven original services remained running.

- [Native probe source](../../../publish/json-verification/aot-source/JsonProbe/Program.cs)
- [Production source manifest](../../../publish/json-verification/aot-source/source-manifest.json)
- [Native publication diagnostics](../../../publish/json-verification/native-build.log)
- [On-device results and ELF inspection](../../../publish/json-verification/native-json-result.json)

## Remaining work

EF compiled models, compatible query execution, and migration deployment remain separate tasks. A standard EF migration bundle uses a regular .NET runtime; the proposed separate-bundle design needs a successful on-device runtime test before it can solve the QNAP migration problem.
