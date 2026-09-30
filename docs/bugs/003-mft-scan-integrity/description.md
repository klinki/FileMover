# Bug description

## Title

MFT scan integrity, review issue 1.

## Status

- awaiting-user-confirmation

## Reported symptoms

Native output buffers are parsed as raw FILE records and failed reads are silently skipped. A scan can mark populated inventory entries missing.

## Expected behavior

Decode the native header, walk returned record numbers downward, and fail scans on unexpected enumeration errors.

## Actual behavior

Native output buffers are parsed as raw FILE records and failed reads are silently skipped. A scan can mark populated inventory entries missing.

## Reproduction details

The review reproduced rejection of a wrapped native output buffer. Scanner only checks per-entry errors before marking unseen entries missing.

## Affected area

MFT decoder and enumerator, Scanner, MftTests.

## Constraints

Implement the approved [review fixes plan](../../features/review-fixes/implementation-plan.md). Junction handling is excluded. Preserve existing records and unrelated changes.

## Open questions

None for implementation. User confirmation follows local verification.
