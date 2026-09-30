# Initial findings

## Confirmed facts

The review imported into an unscanned DB; KEEP succeeded and TRASH conflicted despite the verified survivor.

## Likely cause

Imported KEEP plus TRASH plans cannot find a survivor because ListCompletedCopies excludes KEEP and the fresh database has no inventory.

## Unknowns

Environment confirmation is pending; native MFT I/O requires elevated Windows NTFS access.

## Reproduction status

Confirmed during the repository review. The current implementation still has the reported behavior.

## Evidence gathered

Code inspection and the earlier review reproductions. Planned regression checks: Fresh imported KEEP/TRASH succeeds; changed, missing, or self-referencing survivors refuse trash.

## Verification update, 2026-09-30

Completed KEEP destinations are survivor candidates. Normalized path comparison excludes the victim, and survivor size/hash are checked on disk before TRASH. Regression checks passed in the final suite. User confirmation remains pending.
