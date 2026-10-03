# NAS container preparation

## Goal

Prepare a transferable Docker image and a repeatable first inventory run on the QNAP NAS. The repository's current hardware target is the TS-431P3, ARMv7 with a 32 KiB kernel page size. Confirm the actual NAS model before deploying.

## Scope

- Use QNAP Container Station's Docker engine to run the CLI locally against a NAS share.
- Build the existing ARM32 image on the development PC and export a Docker image archive for Container Station import. Do not publish an image to a registry.
- Mount inventory content read-only at `/data` and a separate persistent state directory at `/state`.
- Run compatibility checks, register the root, perform a complete scan, and hash with conservative parallelism. Reuse hashes on later runs.
- Keep the desktop planner on the PC. Execution against writable NAS data is outside this first test.

## Implementation

1. Limit the build context to the CLI, core project, and container scripts. Preserve the existing Alpine 3.17 ARM32 compatibility baseline and host-native SDK build stage.
2. Add a one-shot inventory script that checks the expected platform and SQLite loading, then runs root registration, scanning, and hashing. Propagate failures and allow configuring the root ID, database path, and hash parallelism through environment variables.
3. Add a Compose application with required NAS share/state paths, a read-only content mount, interactive progress, and no automatic restart.
4. Add a PowerShell build helper that creates an importable image archive and copies the Compose file into a deployment folder.
5. Document Container Station import, direct Docker commands, a small-folder first run, progress, reruns, and copying the stopped inventory database with any WAL companion back to the PC.

Container verification also reproduced one-character hashing progress with an unspecified terminal width. The follow-up is recorded as attempt 002 of the existing hash-progress bug and adds a fallback width for container logs.

## Verification

- Validate Compose interpolation and read-only mounts without accessing a NAS share.
- Build the ARM32 image, inspect its architecture, and check that the publish includes the bundled SQLite library.
- Exercise the entry script with a stub CLI and injected failures; verify later steps do not run after a failed step.
- Attempt application smoke checks under local ARM emulation. Emulation uses the PC kernel and cannot verify the NAS's 32 KiB page size.
- Leave actual NAS compatibility pending until the image passes the checks on that device. No NAS connection or credentials have been supplied.

## Delivery

Provide a local image archive, deployment configuration, and a guide. Record completed checks and any unavailable checks in the feature folder. Commit only when requested.
