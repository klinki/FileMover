# Initial findings

## Confirmed facts

The review staged under A, applied B, then executed an export that moved B's file while leaving A unchanged.

## Likely cause

Changing BasePath after staging exports old relative operations against a different directory.

## Unknowns

Environment confirmation is pending; native MFT I/O requires elevated Windows NTFS access.

## Reproduction status

Confirmed during the repository review. The current implementation still has the reported behavior.

## Evidence gathered

Code inspection and the earlier review reproductions. Planned regression checks: JSON and DB exports remain under A after edits or rejected Apply; clear/final removal unlocks the base and refreshes virtual folders.

## Verification update, 2026-09-30

Added AppliedBasePath and CanChangeBase. Staging and exports use the applied base, base changes are guarded and disabled while staged, and empty queues clear virtual folders and refresh panels. Regression checks passed in the final suite. User confirmation remains pending.
