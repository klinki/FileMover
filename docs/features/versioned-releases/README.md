# Identify and package application builds

```powershell
.\BackupNormalizer.exe --version
.\BackupNormalizer.exe --version --json
.\BackupNormalizer.Ui.exe --version
.\scripts\package-release.ps1
```

The application version is shared across CLI, core, and UI. Version output includes the Git revision, Release/Debug configuration, runtime identifier, and source state. The UI title also shows the version and abbreviated revision. The UI version command exits before initializing Avalonia or opening a window.

Packaged builds mark their application inputs as `clean` or `dirty`. An ordinary build without verified provenance reports `unknown` source state and an `.unverified` display suffix. Source Link embeds the Git revision through the [.NET SDK informational version](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#sourcerevisionid). The revision alone does not prove that local changes were absent.

The [packaging script](../../../scripts/package-release.ps1) requires PowerShell 7.2 or later, Git, and the .NET 10 SDK. It defaults to self-contained Windows x64 builds. `-Runtime` selects another supported publish target; `-OutputDirectory` selects the artifact directory. NuGet access may be needed to obtain runtime packs.

The ZIP contains `cli/`, `ui/`, a build manifest, and a short usage file. Both applications include .NET. The adjacent `.sha256` file identifies the archive contents. Artifact names include version, abbreviated revision, runtime, and a unique run identifier; dirty application sources also add `-dirty`.

The manifest records the full revision, source state, source changes, build-input fingerprint, runtime, and UTC build time. Packaging checks application sources before and after publication and refuses to finish if they change. NAS deployment changes and IDE settings are outside the Windows application build inputs.

## Verification on 2026-10-03

- Version and scan-progress checks: 21 passed, zero failures.
- Full Release suite: 236 passed, 20 environment-dependent skips, zero failures.
- Self-contained Windows x64 CLI and UI publication succeeded.
- Both published `--version` commands exited successfully and matched the package manifest; the UI command opened no window.
- Archive inspection confirmed both executables, both bundled .NET runtimes, the manifest, and usage file. The adjacent SHA-256 matched the ZIP.
- Dirty-source labeling was exercised during pre-commit publication. The delivery package is regenerated after the commit to identify the committed application sources.

Actual elevated USN/MFT execution remains blocked by the canceled UAC request, as recorded in the [native verification log](../native-ntfs-validation/verification.md). Other publish targets have not been run on their respective operating systems in this session.
