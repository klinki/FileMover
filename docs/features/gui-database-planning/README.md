# Database plan review

Database plan review creates a plan for the complete roots selected in the left and right inventory panels. The left-to-right and right-to-left actions choose the source and target explicitly. The current folder shown in either panel does not narrow the plan.

The UI calls the core `Planner` with the selected source database opened read-only and a temporary snapshot of the target database opened writable. Planning changes only that temporary copy. The copy and its SQLite companions are removed on success or failure before the review window appears. Plan metadata keeps the original source database path, root IDs, and recorded physical root paths. The workflow does not execute filesystem operations.

Planning retains the core checks for successful complete scans, required source hashes, a writable target root, disjoint source and target paths, and immutable plan IDs. A target file with different known content appears as a `VERIFY` conflict; the executor checks it and refuses to overwrite a mismatch. Missing target hashes are explained because `VERIFY` checks the destination at execution time. Recorded links remain visible as `SKIP_LINK` rows. File-versus-directory path collisions block JSON export and include the conflicting paths in the review.

The review shows operation counts, estimated bytes copied, recoverable trash operations, full root paths, conflicts, and link skips. Export writes executor-compatible `PlanDoc` JSON. It refuses destinations matching either selected inventory database or its SQLite companion files and writes through a temporary file before replacing an existing export.

## Use plan review

Load an inventory in each panel, select a root in each root list, and enter the plan ID. Choose **Plan left → right...** or **Plan right → left...**. The review shows both recorded root paths and uses the complete selected roots, even when the panels are browsing inside subfolders.

Review the operation list, counts, bytes to copy, trash operations, conflicts, and skipped links. Choose **Export plan JSON** to save the executor-compatible plan. A file-versus-directory conflict disables export until the path issue is resolved. A same-path content mismatch appears as `VERIFY`; the executor checks the destination and refuses to overwrite it. Plan review never executes operations.

## Implementation notes

Panel source or selected-root changes invalidate an in-flight plan build. The UI opens the review in a separate window after asynchronous planning completes. The implementation is in [MainViewModel.Planning.cs](../../../src/BackupNormalizer.Ui/ViewModels/MainViewModel.Planning.cs), [DatabasePlan.cs](../../../src/BackupNormalizer.Ui/Models/DatabasePlan.cs), [DatabasePlanReviewViewModel.cs](../../../src/BackupNormalizer.Ui/ViewModels/DatabasePlanReviewViewModel.cs), and [DatabasePlanReviewWindow.axaml](../../../src/BackupNormalizer.Ui/Views/DatabasePlanReviewWindow.axaml). Fixture coverage is in [UiDatabasePlanTests.cs](../../../tests/BackupNormalizer.Tests/UiDatabasePlanTests.cs).
