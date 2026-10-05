# EF Core Native AOT trial

This tool tests whether EF can read and write the application's SQLite schema on the ARMv7 QNAP. It uses generated fixture databases and temporary copies. It does not scan NAS shares or execute file plans.

The [application-facade checks](Program.cs) use the actual `Database` methods. The [static-query checks](StaticTrial.cs) use the actual context/entities with candidate query shapes. A passing static suite does not establish compatibility of the existing facade.

## Prepare fixtures and establish the baseline

Run from the repository root. Choose a new output folder; snapshot preparation refuses to overwrite earlier experiments. The snapshot pins the existing SDK 10.0.302 and restricts NuGet sources to the local cache and public NuGet.

```powershell
python tools/BackupNormalizer.AotTrial/prepare-snapshot.py publish/ef-aot-trial-local
Push-Location publish/ef-aot-trial-local/source
dotnet run --project tools/BackupNormalizer.AotTrial/BackupNormalizer.AotTrial.csproj -c Release -- --prepare ../fixtures
dotnet run --no-build --project tools/BackupNormalizer.AotTrial/BackupNormalizer.AotTrial.csproj -c Release -- --run ../fixtures
dotnet run --no-build --project tools/BackupNormalizer.AotTrial/BackupNormalizer.AotTrial.csproj -c Release -- --run-static ../fixtures
Pop-Location
```

The current-schema fixture comes from the application's migrations. The legacy fixture stops at the initial migration. Fixture preparation rejects existing database files. Every check works on a new temporary copy, and failed checks return a nonzero process exit code.

## Publish the bounded AOT experiment

[Enable AOT](enable-aot.py) only in the copied sources. The `--static --manual` mode generates the production model and precompiles only the candidate queries. It bypasses the snapshot's runtime migration call because the trial supplies pre-migrated fixtures. Production source stays unchanged.

```powershell
python tools/BackupNormalizer.AotTrial/enable-aot.py publish/ef-aot-trial-local --static --manual
wsl -d Ubuntu -- sh /mnt/c/ai-workspace/FileMover/deploy/build-ef-aot-trial.sh /mnt/c/ai-workspace/FileMover/publish/ef-aot-trial-local /mnt/c/ai-workspace/FileMover/publish/nas-test-20261005 --manual
```

The [build script](../../deploy/build-ef-aot-trial.sh) uses the isolated Linux SDK, NuGet configuration, ARM sysroot, and compiler wrapper from the earlier NAS investigation. Those prerequisites currently live in the ignored `publish/nas-test-20261005` output. This first trial does not yet provide a clean-machine toolchain installer or release image build.

The explicit generation workflow builds managed Linux assemblies without an ARM runtime identifier, runs matching EF CLI 10.0.12, and then publishes the generated sources for `linux-musl-arm`. EF generation belongs to this exact snapshot; regenerate it after source edits. The linker retains the tested 64 KiB load alignment.

Local context variables in the static checks are intentional. The tested generator rejected direct method-parameter context roots. The numeric update uses the expression setter overload because the value setter triggered a generator exception. These shapes passed on the actual NAS.

Omit `--static --manual` from setup and `--manual` from the build command to investigate full EF MSBuild model/query generation. The current facade has unsupported dynamic query shapes, so that experiment is expected to fail. Preserve its log and use a fresh output directory for a different experiment.

## Test on the NAS

Use an existing SSH identity and the previously imported Alpine 3.17 test image. Supply the NAS hostname or address that identity can reach.

```powershell
python tools/BackupNormalizer.AotTrial/test-nas.py publish/ef-aot-trial-local --host nas
```

The [NAS runner](test-nas.py) transfers the AOT executable, SQLite library, and synthetic fixtures into a new image. It checks binary alignment and transfer hashes, runs static and facade suites separately, and verifies original services remain running. Containers have no share mounts, a read-only root, no network, and writable temporary storage. Images and stopped containers are preserved for inspection.

The process succeeds only if the static suite passes on ARM with 32,768-byte pages and dynamic code disabled. The facade result is recorded separately because its queries have not been precompiled in this bounded experiment. Console logs and machine-readable evidence go into the selected output folder.

See [verification](../../docs/features/native-aot-cli/verification.md) for actual results and remaining work.
