# Fix attempt 001

## Attempt status

`awaiting-user-confirmation`.

## Goal

Show useful live hashing feedback without changing digests or inventory semantics.

## Relation to previous attempts

First attempt for this bug.

## Proposed change

1. Add an optional byte-read callback to the hasher's streaming methods.
2. Add scanner snapshots with total and completed file counts, outcomes, bytes read, elapsed time, and the current path. Serialize snapshots across parallel workers and report every file outcome.
3. Add a synchronous, throttled CLI renderer for interactive output. Show file percentage, bytes read, throughput, elapsed time, and path. Support `--no-progress` and keep redirection silent apart from the existing summary.
4. Document the behavior and add focused regression tests for chunk updates, unchanged digests, parallel counts, cache reuse, failures, and empty inventories.

## Risks

- Concurrent callbacks can reorder counts or corrupt terminal output; serialize them and avoid asynchronous `Progress<T>` dispatch.
- Updates only after file completion would still leave a large file silent; report each read chunk.
- Frequent writes can slow hashing; throttle terminal rendering.

## Files and components

- Core hasher and scanner
- CLI hash command and help
- README and hashing regression tests

## Verification plan

Run focused hashing tests, a generated-data CLI terminal smoke check, and the full test suite. Verify both interactive progress and quiet redirected output. Build a usable CLI and request the user's confirmation on their hash command.

## Implementation summary

- Added optional callbacks for each SHA-256 read chunk. Existing callers can omit them.
- Added `HashProgress` snapshots with file outcomes, byte counts, elapsed time, and path. Parallel workers serialize callbacks and counters; an observer failure does not affect hash results.
- Added synchronous terminal progress, throttled to one update per 200 ms with a final update. Root hashing and `--needed` use the same renderer. Progress is disabled by `--no-progress` or redirected stdout.
- Documented progress and cache reuse in the README and CLI help.
- Built the updated CLI at `src/BackupNormalizer/bin/hash-progress/BackupNormalizer.exe`. The regular Debug output is locked by an existing process, so that process continues using its existing binary.

## Test results

- Seven focused regression checks passed: chunked streaming and digest equality, parallel monotonic totals and updates before file completion, cached/missing/unreadable entries, empty inventory, failing observer, and both CLI forms with `--no-progress`.
- Full suite: 121 passed, 8 skipped, 129 total.
- Generated-data Windows terminal check: read 1 GiB across two files. The display showed `0/2 files` while byte counts increased during the first large file, then reached `2/2 files (100%)` before the summary.
- Interactive `--needed` showed cached entries finishing; redirected `--needed` produced exactly its existing summary. Interactive `--no-progress` also produced only the summary.
- CLI build in the separate output folder succeeded without warnings or errors. `git diff --check` passed.
- Initial compile checks exposed string constants and the existing `Digest` property naming; corrected before verification. Fixture cleanup initially encountered SQLite pooled handles; clearing that fixture's pool resolved the test cleanup failure.

## Outcome

Locally verified. User confirmation is pending; the bug remains open.

## Next step

Ask the user to run the updated executable against their inventory database and confirm that progress is visible.

## Remaining gaps

User confirmation is required before marking the bug fixed.
