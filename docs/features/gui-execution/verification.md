# Desktop execution verification

Implemented and checked on 2026-10-04.

- Eight new tests passed for copy and hash progress, cancellation at operation
  boundaries, reopening and resume, immutable roots, changed-plan rejection,
  inventory database protection, conflict handling, verification with an offline
  source, ordered copy/move destinations, and desktop bindings and shutdown.
- The full Release suite passed with 323 passed, 20 skipped, and 343 total.
- Existing skips cover optional native filesystem and headless input tests.
- The build reported the seven existing Avalonia constructor and xUnit warnings.
- The compact main-window layout and execution window passed headless layout
  checks. The execution operation list remains usable at 800 by 660 pixels.
- CSharpier checked all 111 C# files successfully. `git diff --check` passed.

The automated desktop checks use the headless Avalonia host. Native folder
pickers and platform window decorations were not exercised. Cancellation waits
for the current file operation; it does not interrupt an in-progress copy or hash.
