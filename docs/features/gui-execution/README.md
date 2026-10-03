# Desktop execution and verification

Run a reviewed plan from the desktop app with operation statuses, copy and hash
byte counts, elapsed time, and execution logs. The app uses the same executor and
destination verifier as the CLI.

## Start a run

1. Stage manual operations, then choose **Review / execute...** beside the panel
   controls or **File → Review / execute staged plan...**. For database-generated
   plans, choose **Review / execute...** in the plan review window. Blocking type
   conflicts prevent execution.
1. Check the operation order and local source and target roots. Map recorded paths
   to the mounted drives on this computer. A plan that copies from another root
   requires an available source and a disjoint target. Manual plans use their
   target root for drive-local operations.
1. Choose a new execution database outside both operated trees. The default is a
   unique file under the current user's local application data. The app rejects
   existing files for new runs and rejects inventory databases for resume.
1. Choose **Execute reviewed plan...**, review the confirmation, and approve it.
   Opening this window alone does not create a journal or change files.
1. Watch the operation statuses, byte counts, and progress. Expand **Execution log**
   for the recorded history. **Stop on first error or conflict** and automatic
   destination verification are enabled by default.

Execution checks content preconditions, refuses overwrites with different
content, verifies temporary copies before renaming, skips links, and moves trash
into the recoverable `.backup-normalizer-trash/<plan-id>/` directory. Other
desktop jobs and root editing are disabled during a run. Live panels refresh
after it ends; inventory snapshots still reflect their recorded scan.

## Cancel and resume

**Cancel after current operation** lets the active copy, hash, move, or trash
operation finish, then stops before the next operation. A large operation may
take time to finish. Closing the execution window or application requests the
same cancellation and waits for the worker.

Choose **File → Open execution database...** to reopen a saved run. Completed
statuses and logs come from the journal. **Resume execution...** retains completed
operations and retries the remaining operations after confirmation. Resolve
reported conflicts before retrying.

The first execution binds its effective roots. Reopened runs lock those paths
and reject a changed plan. To replay the original relative operations on another
drive, use a new execution database. Keep exported plan JSON for that purpose;
the desktop can also review a freshly generated or staged plan.

## Verify destinations

Choose **Verify destinations** to check the final file destinations against
recorded sizes and hashes. Verification reads files without changing their
contents. Source files are not required for this check. Errors appear in the
verification column and activity log.

Verification skips link operations, mkdir and trash operations, and intermediate
destinations consumed by a later completed move or trash operation. A copy
followed by a move verifies the file at its final path. Verification reads the
current destination bytes; execution statuses and logs remain in the journal,
while verification results are displayed for the current session.

The CLI uses the same check:

```powershell
dotnet run --project src/BackupNormalizer -- verify <plan-id> --db ./execution.db
```

See the [implementation plan](implementation-plan.md) and
[verification results](verification.md).
