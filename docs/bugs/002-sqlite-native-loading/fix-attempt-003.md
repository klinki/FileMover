# Fix attempt 003

## Attempt status

Experiment complete. Ubuntu 22.04 works; CoreCLR 10.0.10 fails before managed entry. The bug remains open.

## Goal

Determine whether a normal self-contained `linux-arm` build runs on the ARMv7 QNAP with 32,768-byte pages. Test managed startup first, then SQLite and the current CLI if startup passes.

## Relation to previous attempts

[Attempt 002](fix-attempt-002.md) established that CoreCLR 10.0.10 crashes on Alpine 3.17, while Native AOT probes work. This attempt changes the C library and runtime target without changing production source or adopting Native AOT.

## Evidence and proposed change

The user supplied [qnap-32k-containers](https://github.com/laroy-sh/qnap-32k-containers), which reports Ubuntu 22.04 working on a TS-431P3 and newer Alpine, Debian, and Ubuntu userlands failing ELF alignment checks. Its results establish a promising userland baseline, not .NET compatibility.

Publish the current CLI and an independent probe with Linux SDK 10.0.302, runtime 10.0.10, and `linux-arm`. Build an isolated image from ordinary `ubuntu:22.04` for `linux/arm/v7`, including the required native runtime dependencies. Retain build and on-device evidence under the ignored publish directory.

## Risks and verification plan

- The QNAP kernel and page size remain unchanged; a working shell does not establish CoreCLR compatibility.
- Test only synthetic files and databases in new containers, with no existing NAS share mounts.
- Record the base image digest, installed dependencies, runtime version, and actual image sizes.
- Check managed entry, SHA256, file operations, SQLite, and CLI scan/hash workflows when startup allows them.
- Compare existing running services before and after the tests.
- Preserve existing application changes and all earlier investigation records. Do not commit or push.

## Implementation and results

Published an independent probe and the current CLI using the existing Linux SDK 10.0.302, runtime 10.0.10, and `linux-arm`. The 51 source/project files in the isolated CLI snapshot still match production source hashes. No production code, target framework, or deployment configuration was changed.

Pulled ordinary `ubuntu:22.04` for ARMv7 onto the actual NAS. The image reports Ubuntu 22.04.5 LTS, glibc 2.35, ARMv7, kernel 4.2.8, and 32,768-byte pages. Base digest: `sha256:5ec03bb3441e8b0bf3b4f9cd4629a1ae763010dc3035bb8da3ae6cf026486401`.

Installed only the native runtime dependencies and created a separate test image containing the CLI, probe, and four synthetic files. The retained image is `backup-normalizer:ubuntu-app-test-20261005`; its default entrypoint is `/app/BackupNormalizer`. A separate diagnostic image adds strace. The initial Docker build submission was rejected because QNAP's legacy builder interpreted a PAX tar header as Dockerfile text; submitting the Dockerfile directly resolved the build-input issue.

Measured uncompressed Docker image sizes:

- Ordinary Ubuntu base: 56,630,839 bytes, approximately 56.6 MB.
- Ubuntu with native .NET dependencies: 95,795,955 bytes, approximately 95.8 MB.
- Complete test image, including self-contained CLI, probe, and fixtures: 180,196,565 bytes, approximately 180.2 MB. This is an experimental image, not an optimized release build.

On-device checks:

- Ubuntu shell, platform commands, and dependency installation passed.
- All 16 published ARM ELF binaries have load offsets and virtual addresses congruent at 32 KiB boundaries.
- Default .NET probe startup failed before managed entry, exit 137, reporting failure to load `System.Private.CoreLib.dll`, HRESULT `0x8007000E`, and `Out Of Memory`.
- Disabling ReadyToRun produced the same failure.
- Disabling executable-memory dual mapping, alone or with ReadyToRun disabled, produced SIGSEGV, exit 139.
- The current CLI's `--version --json` failed with the same CoreLib startup error. SQLite, scan, and hash workflows were consequently not run.
- Docker reported no OOM kill. The memory diagnostic reported 5,726,080 KiB available, unlimited process address space, and no restrictive container memory limit.
- SHA256 checks confirmed intact transfer of the apphost, CoreLib, CoreCLR, JIT, and SQLite binaries.
- All seven original services remained running. Eleven new test containers were inspected; none has a host share or persistent volume mount. Runtime tests used a read-only root filesystem, no network, and writable temporary memory storage. The tracing container added only `SYS_PTRACE` to its otherwise dropped capabilities.

## Trace finding

CoreLib's file mapping succeeds. During subsequent executable-memory allocation, the trace records:

```text
mprotect(0x733ac000, 16384, PROT_READ|PROT_WRITE) = -1 EINVAL (Invalid argument)
```

The starting address has a 16,384-byte remainder when divided by 32,768, so it is not aligned to the actual kernel page size. The trace establishes a misaligned memory-protection request in the failing startup sequence. It supports a runtime page-alignment problem rather than exhaustion of physical RAM; the exact CoreCLR source location has not been identified. ELF alignment alone cannot prevent this dynamic allocation failure.

Local evidence retained in this checkout:

- [Ubuntu base results](../../../publish/ubuntu-test-20261005/base-result.json)
- [Runtime results and image metadata](../../../publish/ubuntu-test-20261005/runtime-result.json)
- [Memory and runtime-setting diagnostics](../../../publish/ubuntu-test-20261005/diagnostics-result.json)
- [System-call trace](../../../publish/ubuntu-test-20261005/runtime-strace.log)
- [File integrity, source snapshot, service, and mount checks](../../../publish/ubuntu-test-20261005/evidence-verification.json)
- [Native binary alignment inspection](../../../publish/ubuntu-test-20261005/elf-inspection.json)

Build scripts and artifacts are under the ignored publish directory. No image was pushed and no changes were committed.

## Outcome

Ordinary Ubuntu 22.04 is usable on this NAS, confirming the supplied repository's userland finding. Switching from `linux-musl-arm` to `linux-arm` is insufficient to make the tested .NET 10 runtime or application usable. A newer Alpine image is not a suitable fallback based on that repository's compatibility results.

The previously verified Native AOT path remains viable for the tested probes. Full application AOT model/query/migration work remains pending. A .NET 11 CoreCLR probe or a runtime-level alignment repair would need separate verification; neither was tested or implemented here. The bug remains open, with no application repair claimed.
