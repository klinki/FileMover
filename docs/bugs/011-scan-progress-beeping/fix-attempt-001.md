# Fix attempt 001

## Attempt status

`awaiting-user-confirmation` after local verification.

## Goal

Make scan progress safe for interactive terminals and verify the rebuilt application with the user.

## Relation to previous attempts

First attempt for scan-time terminal beeping. The separate hash-progress bug remains unchanged.

## Proposed change

- Use the renderer directly as synchronous `IProgress<ScanProgress>` instead of queuing callbacks through `Progress<T>`.
- Limit redraws to one per 200 milliseconds and force the latest snapshot before printing the summary.
- Fit output to the terminal width, retaining the existing 120-column fallback convention when width is unavailable.
- Replace terminal control characters in displayed text with spaces.
- Preserve `--no-progress`, redirected-output behavior, and all scanning and database operations.

## Risks

The exact audible mechanism remains unconfirmed. These changes address demonstrated progress-output defects and require a user retest of the original symptom. Throttling reduces intermediate redraws while the final counts remain visible.

## Files and components

- [CLI implementation](../../../src/BackupNormalizer/Cli.cs)
- [CLI project](../../../src/BackupNormalizer/BackupNormalizer.csproj)
- [Scan progress tests](../../../tests/BackupNormalizer.Tests/CliScanProgressTests.cs)

## Verification plan

Test narrow and unknown terminal widths, control-character suppression, redraw frequency, final counts, shrinking lines, output failures, and summary-only scanning. Run focused Release regressions, rebuild Release, and request user confirmation with progress enabled.

## Implementation summary

The scan renderer now implements `IProgress<ScanProgress>` directly. It redraws at most every 200 milliseconds, flushes the latest snapshot before the summary, and ignores reports after finishing. Text is bounded to the available columns, reserves space for wide characters, and replaces terminal control characters with spaces. Old text is cleared within the current width, including after a resize. Tests receive their own writer and terminal-width provider through an internal constructor.

## Test results

- 24 focused Release tests passed, covering CLI scan progress, scanner progress callbacks, and existing hash progress.
- The full Release suite passed: 162 passed, 17 skipped, zero failures, 179 total. The existing file-symlink, elevated MFT, and headless-interaction skips remain unchanged.
- An interactive PTY scan of 256 temporary regular files displayed progress, completed with zero errors, and stored all 256 entries. The terminal capture also included PowerShell's normal OSC window-title sequence, which terminates with BEL; this is separate from the renderer's text output and does not establish the source of the user's sound.
- Regression checks cover narrow and unknown widths, wide text and surrogate pairs, BEL and other control characters, throttling, final counts, shrinking paths, resizing, observer output failures, and summary-only scanning.
- Rebuilt the solution in Release: zero errors and four existing warnings. No source database changes were made.
- Initial test setup needed a distinct class name from the existing scanner-progress tests and CRLF-aware parsing of the final newline. Both were corrected before the passing runs.
- Reviewed the diff and preserved the pre-existing hash-progress width fallback and other unrelated changes.

## Outcome

The demonstrated scan-progress rendering defects are repaired and the Release output is rebuilt. The reported audible symptom remains pending user confirmation in Windows Terminal. No commits or pushes were made.

## Next step

Replace the CLI's Release folder on the user's computer and repeat scanning with progress enabled. Request confirmation that the beeping stops.

## Remaining gaps

Audible validation in the user's Windows Terminal.
