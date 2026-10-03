# Fix attempt 002

## Attempt status

`awaiting-user-confirmation` after local verification.

## Goal

Show each failed scan path and its reason on stderr, including directory-enumeration failures that are not stored as file entries.

## Relation to previous attempts

Attempt 001 remains locally verified for progress rendering. The user's subsequent scan revealed missing error diagnostics in the same CLI scan flow. This attempt preserves attempt 001's changes and does not imply confirmation of the beeping fix.

## Proposed change

- Add an optional synchronous scanner callback with root, path, and error message.
- Report enumeration errors, metadata errors, and fatal scan failures through that callback.
- Collect CLI diagnostics per root, finish the progress line, then write diagnostics to stderr before the normal stdout summary.
- Preserve error counts, scan status, continuation, unseen-entry preservation, and non-fatal link notes.
- Keep diagnostics enabled with `--no-progress` and redirected stdout; sanitize terminal control characters in diagnostic text.

## Risks

Diagnostic observers must not change scan results. The CLI temporarily holds error messages until each root finishes so progress cannot overwrite them. Existing behavior for unavailable filesystem areas remains incomplete rather than incorrectly declaring completeness.

## Files and components

- [Core scanner](../../../src/BackupNormalizer.Core/Scanner.cs)
- [CLI](../../../src/BackupNormalizer/Cli.cs)
- [Scan diagnostic tests](../../../tests/BackupNormalizer.Tests/ScanErrorTests.cs)

## Verification plan

Use deterministic enumeration and invalid-metadata fixtures to check paths, messages, counts, continuation, and preservation of unseen entries. Test failing observers, non-fatal link notes, fatal-root stderr output, and `--no-progress`. Run focused and full Release regressions and rebuild the application.

## Implementation summary

Added a typed `ScanError` diagnostic and an optional synchronous callback to the core scanner. Enumeration failures, per-entry metadata failures, and fatal scan failures provide the root ID, full path, and message. Callback failures are isolated from scanning. The CLI collects diagnostics for each root and prints sanitized messages to stderr after finishing the progress line, before the stdout summary. `--no-progress` does not suppress errors.

## Test results

- 26 focused Release checks passed, including seven new diagnostic cases, the existing scan-progress checks, and enumeration-failure preservation regressions.
- Full Release suite: 169 passed, 17 skipped, zero failures, 186 total. Existing skips are unchanged.
- Deterministic enumeration and metadata errors each produced one diagnostic while unaffected files continued and unseen entries retained their prior state.
- A throwing diagnostic observer did not change scan results. Unavailable link metadata continued to produce notes with zero scan errors and zero error callbacks.
- CLI checks confirmed fatal-root diagnostics go to stderr with and without `--no-progress`, and terminal control characters are sanitized.
- Release rebuild succeeded with zero errors and four existing warnings. No user inventory was modified during verification.

## Outcome

Locally verified and rebuilt. The user's original two errors can be identified on the next scan with the rebuilt application. No commits or pushes were made.

## Next step

Copy the entire rebuilt CLI Release folder and rerun the scan. Capture stderr with `2> .\scan-errors.log` if useful. Request user confirmation that paths and reasons are visible.

## Remaining gaps

The user's two underlying filesystem errors are not yet identified; the rebuilt CLI will expose them on the next scan.
