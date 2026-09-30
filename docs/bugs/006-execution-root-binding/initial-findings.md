# Initial findings

## Confirmed facts

The review executed on A, then overrode the target to B; B remained untouched while execution reported completion.

## Likely cause

Completed statuses are reused when execution is remapped to another drive, reporting completion without applying operations there.

## Unknowns

Environment confirmation is pending; native MFT I/O requires elevated Windows NTFS access.

## Reproduction status

Confirmed during the repository review. The current implementation still has the reported behavior.

## Evidence gathered

Code inspection and the earlier review reproductions. Planned regression checks: First-run overrides, restart/resume, mismatched root rejection without state changes, fresh JSON replay, drift conflicts, and EF upgrade preservation.

## Verification update, 2026-09-30

Added the standard BindExecutionRoots EF migration and nullable execution paths. A transaction binds effective paths before operations start. Execution rejects mismatches and unbound historical attempts; retry and verification default to bound paths. Fresh import preserves the JSON format and creates planned statuses. Regression checks passed in the final suite. User confirmation remains pending.
