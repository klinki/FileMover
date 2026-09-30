# Initial findings

## Confirmed facts

The review reproduced rejection of a wrapped native output buffer. Scanner only checks per-entry errors before marking unseen entries missing.

## Likely cause

Native output buffers are parsed as raw FILE records and failed reads are silently skipped. A scan can mark populated inventory entries missing.

## Unknowns

Environment confirmation is pending; native MFT I/O requires elevated Windows NTFS access.

## Reproduction status

Confirmed during the repository review. The current implementation still has the reported behavior.

## Evidence gathered

Code inspection and the earlier review reproductions. Planned regression checks: Synthetic output buffers and record sequences; failed and incomplete scan preservation; elevated NTFS comparison when available.

## Verification update, 2026-09-30

Decoded and validated the native header and payload; read records downward using returned numbers; retained directory ancestry; distinguished extension metadata from malformed records; propagated failures to the scanner with --mft off retry guidance. Regression checks passed in the final suite. Native NTFS comparison is pending because the current Windows token is not elevated. User confirmation remains pending.
