# Initial findings

## Confirmed facts

- The user identified CLI scanning in Windows Terminal and confirmed that `--no-progress` stops the sounds.
- The application has no explicit beep call or literal BEL sequence in its progress renderer.
- Scan progress uses asynchronous `Progress<ScanProgress>` callbacks without throttling. Callbacks can remain queued when the scan finishes.
- The renderer pads every update to at least 110 characters without checking terminal width. A captured ordinary update contains 111 characters including its leading carriage return, and zero BEL characters.
- Displayed root labels and paths are not sanitized. A synthetic path containing U+0007 is emitted as a terminal bell character.
- Read-only inspection of the supplied inventory found no control characters in its recorded relative paths. Its WAL file was empty at inspection. No database changes were made.

## Likely cause

The user's comparison isolates progress output as the trigger. The renderer has confirmed output defects involving width, update frequency, asynchronous completion, and unescaped control characters. None of those observations yet establishes the exact source of the repeated sound in the user's environment.

## Unknowns

The terminal's effective dimensions and settings during the reported run, and whether the sound is produced by BEL output, accessibility feedback, or another terminal behavior.

## Reproduction status

User reproduction is confirmed. Local capture reproduces the oversized line and synthetic BEL emission, but does not reproduce the audible symptom.

## Evidence gathered

- [Current CLI implementation](../../../src/BackupNormalizer/Cli.cs)
- Local reflection-based capture of the rebuilt Release renderer, with ordinary and synthetic control-character paths
- [Microsoft's Windows Terminal bell settings](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-advanced#bell-notification-style), which describe BEL-triggered notification behavior

## Verification update, 2026-10-02

Attempt 001 passed the focused renderer checks and an interactive PTY scan of temporary files. That capture contained the host's OSC window-title terminator, separate from application progress text. The automated text checks confirm that the updated renderer filters BEL and other terminal controls. No claim of audible reproduction or confirmation is made; the user's original terminal still needs a retest.

## Follow-up findings, 2026-10-02

The scanner counts filesystem-enumeration errors from `FsEntry.Error` and immediately continues without exposing their paths or messages. Per-entry metadata exceptions are counted and best-effort recorded in the inventory, but are also silent in the CLI. The CLI receives only aggregate counts. Attempt 002 will provide an optional synchronous error callback, and print every collected diagnostic to stderr after finishing the progress line. Real scan failures will continue to prevent completeness and preserve unseen entries.
