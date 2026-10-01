# GUI database comparison

Each file panel can browse a live filesystem or a read-only SQLite inventory.
An inventory panel selects one root from its database and reconstructs its
directory tree from recorded file paths. It does not access the recorded root
on disk. Two databases can contain the same root ID without combining entries.

Folder comparison starts at the folders displayed in the two panels. It compares
descendants by relative path, reports one-sided entries and file/folder
conflicts, and propagates differences to their parent folders. Matching size
and fresh SHA-256 hashes establish equality. Matching size without usable hashes
remains unverified. Case-insensitive matching requires both roots to record
case-insensitive filesystems.

Linked browsing follows the same relative folder in the other panel, including
folders absent from that inventory. Differences-only filtering hides equal
entries while keeping unverified files visible. Reloading an inventory or
changing roots clears comparison results. Loading and comparison run in the
background; loading a replacement unsuccessfully preserves the displayed data.

Database panels disable filesystem staging, including drag-and-drop and direct
calls to staging methods. The existing live-folder planner remains available
after both panels return to live folders. Staged operations block source changes.

## Limits

- Empty directories are not recorded in the inventory schema.
- Incomplete scans cannot establish that unindexed files are absent. The UI
  displays the scan status and warns when comparing incomplete inventories.
- Database comparison does not generate or execute synchronization operations.
- Each loaded database is held as an in-memory snapshot. Refresh explicitly
  reloads it; an ongoing scan does not update the panel automatically.

## Verification

Automated checks cover offline browsing, root selection and isolation, separate
databases with equal root IDs, subtree comparison, content hashes and stale
hashes, case sensitivity, missing-folder navigation, filtering, scan errors,
failed loads, refresh and swap, read-only loading, and staging guards. Avalonia
checks verify both panel headers, comparison columns, and colored markers.
