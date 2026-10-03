# Native NTFS verification

## Checks on 2026-10-03

The focused Release run passed 52 tests with three permission-dependent skips and no failures. The new parity fixture compiled successfully.

The native fixture compares inventories and SHA-256 digests from USN, recursive enumeration, and required MFT enumeration. It covers 1,000 baseline files, same-size writes with restored timestamps, creation, rename, deletion, file links, directory links, preservation of external contents, and scan timings.

This session has no Windows administrator token. An elevated runner was requested through UAC, but Windows returned `The operation was canceled by the user`. Native execution and real timings therefore remain unverified. No journal was created or modified, and no live inventory was accessed.

Run the [native verification script](../../../scripts/verify-native-ntfs.ps1) from an elevated PowerShell 7 terminal:

```powershell
.\scripts\verify-native-ntfs.ps1
```

The script saves detailed output and a TRX report beneath the workspace's generated publish directory. It fails if either native check skips. Review the recorded fallback reasons and timings before claiming a native speedup.
