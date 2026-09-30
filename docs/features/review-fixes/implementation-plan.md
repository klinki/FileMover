# Fix the six remaining review issues

## Summary

Fix issues 1 and 3–7. Junction handling remains outside this work.

Preserve your workflow: organize under `G:\...`, export relative operations, import into a fresh database with another root, then execute there.

Artifact: `implementation-plan.md`  
Slug: `review-fixes`  
Save path: `C:\ai-workspace\FileMover\docs\features\review-fixes\implementation-plan.md`

## Implementation

### Issue 1. Correct MFT enumeration and scan failure handling

- Allocate space for the native output structure and its record payload. Validate the returned header and length before parsing the payload, following the [Windows API contract](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record).
- Enumerate downward using the returned record number. Include directory ancestry needed for path resolution.
- Distinguish intentional metadata skips from malformed records, failed reads, and unresolved paths.
- Propagate unexpected failures to the scanner. A failed or incomplete enumeration must never mark unseen inventory entries as missing.
- Keep `auto` fallback when MFT initialization is unavailable. Errors after enumeration starts fail the scan with guidance to retry using `--mft off`.

### Issue 3. Lock the UI base while operations are staged

- Separate the editable base text from the successfully applied base. Staging and exports always use the applied value.
- Disable base editing and Apply while the queue contains operations. Enforce the same restriction in the view model for programmatic calls.
- Clearing the queue clears virtual folders and refreshes both panels. Removing the final operation also unlocks the base and clears remaining virtual folders.
- An invalid Apply leaves the applied base, panels, and queue intact.

### Issue 4. Preserve manual operation order

- Remove operation-type sorting from `BuildPlanDoc`.
- Assign sequence numbers in staging order and preserve them through JSON and database import.
- Keep generated MKDIR operations before the operations that need them.
- Automatic planning retains its existing ordering logic.

### Issue 5. Bind execution statuses to the roots actually used

- Add nullable `ExecutionSourceRootPath` and `ExecutionTargetRootPath` fields to plans through a new EF migration. Preserve the initial migration and existing records.
- On first execution, atomically record the normalized effective roots before starting operations. Bind the source only when operations read from the source root.
- Subsequent executions default to those recorded roots. A conflicting override fails before touching files or operation statuses, with instructions to import the original JSON into a fresh database.
- Same-root retries and resume keep their existing completed-operation behavior. Verification defaults to the recorded execution roots and still permits explicit overrides.
- Imported JSON initializes fresh operation statuses and no execution binding. The JSON format and existing remapping flags remain unchanged.
- Previously attempted plans without recorded execution roots require fresh import for further execution, because their historical overrides cannot be established reliably.

### Issue 6. Recognize verified KEEP survivors

- Include completed KEEP operations in `ListCompletedCopies`.
- Continue checking the candidate's current size and full hash on disk before allowing TRASH.
- Exclude the file being trashed from survivor candidates. Imported plans must work without a scanned inventory.

### Issue 7. Exclude the active inventory database

- Exclude the exact active database path and its `-wal`, `-shm`, and `-journal` companions from both enumeration modes and hashing. These companion names follow [SQLite's documented behavior](https://www.sqlite.org/tempfiles.html).
- Compare normalized absolute paths using the existing platform path rules.
- Mark previously inventoried entries for these paths missing and invalidate their hashes, so old entries cannot block planning.
- Ordinary database files elsewhere remain inventory content.

## Verification

| Area | Required regression checks |
|---|---|
| MFT | Wrapped output buffers, invalid lengths, returned record numbers, intentional skips, read failures, and preservation of existing inventory after incomplete scans. |
| UI base | Stage under A, attempt changing to B, and verify JSON and DB exports still reference A. Clearing or removing the last operation unlocks the base and removes virtual folders. |
| Ordering | Stage COPY followed by MOVE from the same source. After JSON import and execution, both destinations contain the expected bytes. |
| Replay | Execute on A, reject rebasing that execution DB to B, then import the JSON into a fresh DB and successfully execute on B. Cover first-run overrides, resume, drift conflicts, and migration. |
| KEEP/TRASH | Fresh imported KEEP plus TRASH succeeds with an intact survivor; missing or changed survivor content prevents trashing. |
| Database exclusion | Store the inventory DB inside the root, scan and hash successfully, and build a plan. Cover existing polluted entries and an unrelated `.db` file. |

Run focused tests during implementation, then the full suite. Run one Windows NTFS comparison against recursive enumeration when elevated access is available; otherwise record that check as pending. Update CLI and UI documentation with the fresh-database replay example.

## Delivery

Save this plan before implementation. Record each repair in a separate bug workspace, continuing numbering from `003`, and preserve existing bug history.

After local verification, request confirmation using the supplied UI and replay checks. The [bug-fixing skill](C:/Users/david/.agents/skills/bug-fixing/SKILL.md) requires: “Until the user explicitly confirms the fix, keep the bug open and record the state as `awaiting-user-confirmation`.”

Commits require a separate request.
