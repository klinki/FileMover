# Fix attempt 004

## Attempt status

Compatibility experiment completed. The official .NET 6.0.36 hello-world builds failed on the actual NAS under both Ubuntu 22.04 and Alpine 3.17. No production repair is claimed.

## Goal and relation to previous attempts

Run a trivial managed program on the actual ARMv7 QNAP with 32 KiB pages using .NET 6. [Attempt 003](fix-attempt-003.md) found that Ubuntu 22.04 works but CoreCLR 10.0.10 fails before managed entry. This experiment checks whether the older runtime behaves differently.

## Test setup

Use the already installed Windows SDK 6.0.428 to publish an independent `net6.0` console program with the official 6.0.36 runtime for `linux-arm`. Copy it into a new test image based on the existing Ubuntu 22.04 image with native runtime dependencies. Print hello world, runtime version, architecture, and actual page size.

After Ubuntu startup failed before CoreCLR loading, publish the same program for `linux-musl-arm` and test it in the previously verified Alpine 3.17 image. This separates the two native runtime distributions without involving application dependencies.

## Verification and limits

Check the exit code and output on the actual NAS. Compare transferred binary hashes and running services. Use only new isolated containers without NAS share mounts. If startup fails, check the executable-memory runtime setting to distinguish the default result from a workaround.

This experiment does not retarget the application, test EF or SQLite, install a global SDK/runtime, or claim full application compatibility. Preserve prior records and unrelated changes. No commit or push is requested.

## Implementation and results

The independent program is:

```csharp
using System.Runtime.InteropServices;

Console.WriteLine("Hello, world from .NET 6!");
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Page size: {Environment.SystemPageSize}");
```

Both self-contained publishes succeeded using SDK 6.0.428 and runtime 6.0.36. The generated runtime configuration confirms `Microsoft.NETCore.App` version `6.0.36`. No global SDK or runtime installation was required.

```powershell
dotnet publish HelloNet6.csproj -c Release -r linux-arm --self-contained true --configfile NuGet.Config -o app
dotnet publish HelloNet6.csproj -c Release -r linux-musl-arm --self-contained true --configfile NuGet.Config -o app-musl
```

| Container base | Runtime identifier | Default settings | `DOTNET_EnableWriteXorExecute=0` |
| --- | --- | --- | --- |
| Ubuntu 22.04.5, glibc 2.35 | `linux-arm` | Exit 139, no output | Exit 139, no output |
| Alpine 3.17 | `linux-musl-arm` | Exit 139, no output | Exit 139, no output |

Exit 139 represents SIGSEGV. Neither build printed its first managed line. Docker reported `OOMKilled: false` for all four runs.

A separate Ubuntu trace run reproduced SIGSEGV with `SEGV_ACCERR` at address `0x541fbd30`. Its captured file-opening calls contain no `libhostfxr.so`, `libhostpolicy.so`, or `libcoreclr.so` load. This failure happens earlier than the .NET 10 Ubuntu failure from attempt 003; the exact failing function has not been identified. Do not attribute it to that attempt's misaligned `mprotect` without further evidence.

Both apphosts and CoreCLR libraries report 4,096-byte ELF load-segment alignment. File offsets and virtual addresses are congruent modulo 32 KiB, but that check alone does not establish correct segment permissions on a 32 KiB-page kernel. The trace suggests an early native loading/protection issue; its exact cause remains unconfirmed.

SHA256 hashes of the apphost, hello-world DLL, CoreLib, CoreCLR, and JIT matched between the PC and NAS for both builds. All seven original services remained running. Test containers had no NAS share mounts, disabled networking, a read-only root, writable `/tmp`, and dropped capabilities. Only the separate trace container added `SYS_PTRACE`.

Retained images:

| Image | Default entrypoint | Uncompressed size |
| --- | --- | --- |
| `backup-normalizer:net6-hello-test-20261005` | `/app/HelloNet6` | 161,888,592 bytes |
| `backup-normalizer:net6-musl-hello-test-20261005` | `/app/HelloNet6` | 162,354,232 bytes |

Local artifacts are ignored build/test output:

- [Hello-world source](../../../publish/net6-test-20261005/Program.cs)
- [Project](../../../publish/net6-test-20261005/HelloNet6.csproj)
- [NAS test runner](../../../publish/net6-test-20261005/test-nas.py)
- [Ubuntu results and binary checks](../../../publish/net6-test-20261005/nas-result.json)
- [Alpine results and binary checks](../../../publish/net6-test-20261005/nas-musl-result.json)
- [Ubuntu startup trace](../../../publish/net6-test-20261005/runtime-strace.log)
- [Trace container result](../../../publish/net6-test-20261005/trace-result.json)

## Outcome

The official .NET 6.0.36 self-contained builds do not run even hello world in these two configurations on this QNAP. This does not rule out different .NET 6 patches, custom native builds, or configurations described in the user's articles. No article-specific setup was supplied or reproduced.

The application remains on .NET 10 and was not changed. No EF, SQLite, inventory, or replay checks were attempted with .NET 6. The application compatibility bug remains open; previously successful Native AOT probes remain the demonstrated working route. No commit or push was made.
