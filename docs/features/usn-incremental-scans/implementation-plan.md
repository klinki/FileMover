# USN incremental scans

## Summary

Use the Windows NTFS USN journal to refresh changed inventory entries after a successful full scan. Keep full scanning as the fallback and retain the existing separate hashing step. The user authorized implementation on 2026-10-02.

Artifact: implementation-plan.md

Slug: usn-incremental-scans

Save path: C:\ai-workspace\FileMover\docs\features\usn-incremental-scans\implementation-plan.md

## Scope

- Enable journal use automatically on supported local NTFS roots. Read existing journals without creating or changing them.
- Add `--usn auto|off`, a persisted `usnMode` setting, and `--full` to request a full metadata scan.
- Keep recursive or MFT enumeration for full scans. Other filesystems, network shares, unavailable journals, and insufficient permissions retain full scanning.
- Defer ReFS and other filesystem journal backends, database batching, and hash-buffer pooling.

## Implementation

1. Add a checkpoint table and EF migration. Store the root path and identity, volume identity, journal ID, next USN, and successful scan ID. Invalidate checkpoints when roots move. Older read-only inventories remain readable without migration.
1. Add a disposable Windows journal reader with a deterministic test interface. Validate buffer lengths, record versions, IDs, names, USN ordering, and cursor progress. Read a bounded window using all reasons and immediate notifications.
1. Capture the journal position before full enumeration. Save it only after a successful scan, so changes during enumeration remain available to the next scan.
1. Validate existing checkpoints against the latest successful scan, current root and volume identities, journal ID, and retained history. Resolve changed file paths through parent file IDs without reading file contents.
1. Refresh changed files and links, and mark confirmed deleted paths missing. Preserve untouched entries. Invalidate all algorithms' cached hashes for known content or identity changes, including same-size writes with restored timestamps.
1. Fall back to full scanning for directory namespace or reparse changes, hard-link changes, ambiguous identities, unsupported records, excessive journal windows, or unresolved paths. Discard untrustworthy checkpoints and invalidate affected cached hashes conservatively.
1. Apply incremental inventory changes and checkpoint advancement in one transaction. A failed incremental scan preserves the previous inventory and forces a full scan next time. Keep a checkpoint before still-open relevant changes so coalesced writes are replayed until closure.
1. Preserve link inventory and traversal rules. Recheck parent components before refreshing changed entries, skip database files, and never access linked content.
1. Make CLI progress and summaries distinguish incremental entries refreshed from full-scan entry counts. Explain full-scan fallback reasons.

## Verification

- Deterministic journal parsing and replay fixtures for creates, changes, deletes, file renames, directory fallbacks, hard links, records outside the root, open handles, and malformed records.
- Checkpoint migration, read-only compatibility, invalidation, journal resets and gaps, root or volume replacement, incomplete scans, transaction rollback, and changes during full enumeration.
- Hash reuse for unchanged files and invalidation for content changes, regular-file/link transitions, and deletion/recreation.
- CLI controls and summaries, database exclusion, linked-parent protection, and unaffected inventory preservation.
- Run focused tests and the full Release suite, rebuild Release, and exercise a real Windows journal when current permissions allow. Record environmental skips explicitly.

## Delivery

Preserve unrelated working-tree changes. Do not modify the user's live inventories, commit, or push. Document delivered behavior and validation in this feature directory and the README.

## References

- [Change journals](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journals)
- [Reading the USN journal](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_read_usn_journal)
- [Journal read request](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-read_usn_journal_data_v0)
- [USN version 2 records](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)
- [Opening files by ID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid)
