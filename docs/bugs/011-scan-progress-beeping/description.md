# Scan progress causes repeated terminal beeps

## Status

Open; attempt 002 is locally verified and `awaiting-user-confirmation`. Attempt 001 also remains awaiting user confirmation.

## Reported symptoms

The user hears repeated beeps while scanning with the CLI in Windows Terminal. The beeps appear to coincide with progress updates. The user confirmed that adding `--no-progress` stops the beeping.

## Expected behavior

Interactive scanning shows readable, quiet progress and a final summary.

## Actual behavior

Progress-enabled scanning produces repeated sounds in the user's terminal. The exact terminal mechanism has not been reproduced locally.

## Reproduction details

Run the Release CLI's `scan d` command against the existing drive inventory in Windows Terminal. Compare with the same command using `--no-progress`.

## Affected area

- [CLI scan progress renderer](../../../src/BackupNormalizer/Cli.cs)
- Windows Terminal interactive output

## Constraints

Preserve scan results, metadata, migrations, hashing, and unrelated working-tree changes. Do not modify the user's inventory database during verification. Retain `--no-progress` and quiet redirected output.

## Open questions

Does attempt 001 silence progress-enabled scanning in the user's terminal? The emitted BEL character or other mechanism responsible for the reported sound has not yet been identified.

## Follow-up, 2026-10-02

The user's next CLI scan processed 176,440 entries and reported two errors with status `incomplete`, but did not show the failing paths or reasons. The user requested those diagnostics on stderr. Continue this scan-output repair in attempt 002 without treating the new output as confirmation that the earlier beeping is fixed.
