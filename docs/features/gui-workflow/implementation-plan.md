# GUI inventory workflow

The user approved five GUI improvements on 2026-10-03. Implement each as a separate feature commit. Preserve unrelated workspace changes and do not push.

## Inventory health and diagnostics

Display selected-root scan age, status, full or USN mode, fallback reason, regular/link/error counts, hash readiness, and planning blockers. Provide an error viewer with paths and messages. Read-only and legacy inventories must remain readable without migration or writes.

## Database plan review

Generate a directional source-to-target plan from the selected database roots using the core planner. Review operations, copy bytes, recoverable trash, conflicts, and skipped links before exporting. Keep inventory databases read-only by generating against an isolated working snapshot. Preserve existing execution preconditions. No filesystem operations execute from plan review.

## Search, filters, and details

Add filename/path search and focused inventory filters for unverified entries, conflicts, scan errors, and links. Preserve folder navigation and existing differences-only behavior. Show selected-entry size, timestamps, hash availability, comparison state, link targets, and notes in a persistent details pane.

## Background scan and hash jobs

Offer scan, hash-needed, and scan-then-hash actions for a selected inventory root with a current local path. Run work in the background with progress and cancellation. Retain the last result and safely reload affected snapshots after work finishes. Use core scanning and hashing rules, including link exclusions and incomplete-scan handling. Do not silently elevate or run destructive plan operations.

## Portable export and session restoration

Expose consistent SQLite snapshot export with a destination file picker and clear success/failure messages. Remember panel database paths, root selections, folders, panel split, and column widths. Handle absent databases and folders gracefully. Keep recorded inventory root paths distinct from current local paths and retain offline browsing.

## Verification and delivery

Use deterministic database and filesystem fixtures plus Avalonia headless checks for new controls where practical. Verify no inventory writes from health, comparison, filtering, or plan review. Exercise job cancellation and failure, JSON export, snapshot export, and session restoration. Run focused tests for each batch and the full suite in Release after integration. Record platform-dependent skips. Commit feature by feature without including unrelated NAS/container or IDE changes.
