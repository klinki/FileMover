# Desktop execution and verification

## Goal

Run an explicitly reviewed plan from the desktop app, show operation and byte
progress, preserve execution history, resume a stopped run, and verify resulting
file contents through the existing execution rules.

## Implementation

1. Extend the core executor with optional progress and cancellation at operation
   boundaries. Preserve content checks, crash-safe copies, trash-survivor checks,
   immutable root bindings, and CLI compatibility.
1. Share destination verification between the CLI and desktop runner. Verify the
   final destinations of ordered plans and report errors and skipped links.
1. Add a background desktop execution session with a dedicated SQLite journal,
   root mapping, operation statuses, and persistent logs. Reject inventory databases
   as execution journals and mismatched plans on resume.
1. Add execution entry points from database plan review and staged operations,
   plus an open-existing-execution command. Require user confirmation before a
   run. Disable editing and other jobs while executing. Closing the execution
   window or application requests cancellation and awaits the active operation.
1. Add tests for copy progress, cancellation and resume, changed-root refusal,
   conflicts, verification, read-only inventory protection, and desktop bindings.
1. Format, build, test, document, and commit independently of backup coverage.

## Acceptance

- Opening or reviewing a plan does not modify files.
- Execution uses the existing executor and a separate persistent journal.
- Cancellation leaves completed operations recorded and remaining operations
  available to resume. The current copy/hash may finish before cancellation.
- A restarted run retains its original execution roots and history.
- Verification checks recorded file destinations and reports mismatches.
