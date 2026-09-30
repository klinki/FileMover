# Initial findings

## Confirmed facts

The review confirmed that the move succeeded but the copy failed because its source was already moved.

## Likely cause

BuildPlanDoc sorts MOVE before COPY, breaking a valid COPY followed by MOVE from the same source.

## Unknowns

Environment confirmation is pending; native MFT I/O requires elevated Windows NTFS access.

## Reproduction status

Confirmed during the repository review. The current implementation still has the reported behavior.

## Evidence gathered

Code inspection and the earlier review reproductions. Planned regression checks: COPY then MOVE round-trip creates both desired destinations; generated MKDIR precedes its consumers.

## Verification update, 2026-09-30

BuildPlanDoc preserves staging order and sequential IDs. MKDIR stays before its consumers, and JSON/database import retain the sequence. Regression checks passed in the final suite. User confirmation remains pending.
