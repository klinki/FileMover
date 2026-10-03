# BackupNormalizer.Ui - Inventory comparison and planner

A cross-platform Avalonia UI for browsing inventory databases, comparing
folders, and staging BackupNormalizer plans. Database browsing and comparison
are read-only. Live-folder operations create plans with size and hash
preconditions; the CLI executor applies them later.

## Run

```bash
dotnet run --project src/BackupNormalizer.Ui
```

Requires the .NET 10 SDK. No Docker needed (Docker is only for the QNAP
scanner, see the repo README).

## Compare inventory databases

1. Select **Load database...** above either panel and choose an existing SQLite
   inventory. Each panel can load a different database, or both can load the same
   database with different roots. Choose the inventory root in each panel's list.
2. Browse to the folders to compare, then select **Compare folders**. Comparison
   includes descendants and matches paths relative to the selected folders.
3. The comparison column labels entries **Only left**, **Only right**,
   **Different**, **Equal**, **Unverified**, **Skipped link**, or **Type conflict**. Colored
   markers beside names show the same status. Parent folders reflect differences
   below them. **Differences only** hides equal files, equal folder trees, and skipped links.
4. **Linked browsing** opens corresponding folders in both panels. A folder
   missing from the other inventory displays an empty listing there; the parent
   entry returns to the containing folder. Disable linked browsing to navigate
   independently. Comparing again uses the folders currently displayed.
5. Refresh reloads the database snapshot. Refreshing, selecting another root,
   swapping sources, or leaving the compared folder clears comparison results.
   Select **Compare folders** again after choosing the new folders.
6. Select **Live folders** to return a panel to the filesystem. Both panels must
   show live folders before staging operations. Clear staged operations before
   loading a database.

Recorded root paths are labels, so inventories from offline drives and other
operating systems remain browsable. Equal content requires matching size and
fresh SHA-256 hashes. Missing or stale hashes leave same-size files unverified,
even when their timestamps match. Path comparison ignores case only when both
roots record case-insensitive filesystems.

An incomplete or unavailable scan produces a warning because an unindexed file
may still exist on the drive. Scan-error entries remain visible. Files marked
missing by a rescan are excluded. The inventory schema stores files, so empty
folders cannot appear in this view. This mode compares recorded snapshots and
does not copy, delete, or synchronize files.

Each database panel shows scan and SHA-256 readiness. Select **Inventory health / scan errors...**
to inspect scan age, mode, fallback reason, entry counts, planning blockers, and recorded
error paths and messages. Older inventories show unavailable diagnostic fields as unknown.
See the [inventory health guide](../../docs/features/gui-inventory-health/README.md).

Links show their kind in the size column. Hover over a name to see its target
text, absolute immediate target path, and any metadata note. Linked folders
cannot be opened. Links are excluded from content comparisons and scan-error
counts; a regular entry opposite a link is a type conflict.

Staging records excluded links and blocked paths as `SKIP_LINK` operations.
Their reasons appear in the staged operations window and exported plans.

## Basic usage

1. **Set the base.** `Base` is the logical root everything is
   staged against (e.g. `/Volumes/D1`). Press **Apply**. All staged paths
   are stored relative to the `Root` id (default `disk`), so the same plan
   can later run on another drive via the CLI's `--target-path`.
   The base field and Apply are locked while operations are staged. Clear
   the queue or remove its final operation to choose a new base; this also
   removes staged virtual folders.
2. **Browse.** Two panels, `..` goes up, double-click enters folders.
   Toolbar icons: refresh active panel, swap panels. Double-clicking the
   divider restores 50/50.
3. **Mark what you want.** Cursor (blue row) and marks (red filenames) are
   independent, like in Total Commander:
   - Left click: move cursor. Right click: toggle mark.
   - Hold right button and drag: mark/unmark everything encountered.
     The mode latches from the starting file (unmarked → select only,
     marked → deselect only). Dragging past the edge auto-scrolls.
   - `Ctrl`/`Cmd`+click: toggle one. `Shift`+click: mark range.
   - `Space`: toggle cursor file. `Shift`+↑/↓: move cursor and toggle.
   - `Ctrl`/`Cmd`+`A`: mark all. `Esc`: clear marks. `Tab`: switch panel.
4. **Stage operations** (active panel → other panel as destination):
   - `F5` copy, `F6` move (or drag & drop across panels), `F7` new folder
     (dialog; the folder appears immediately as a navigable virtual dir),
     `F8` delete (staged as recoverable trash, never permanent).
   - Watch the **staged operations** window (toolbar icon): ordered op list,
     `Delete` key removes the selected op after confirmation.
   Operations execute in their displayed staging order, including a COPY
   followed by a MOVE from the same source.
5. **Export.** `Save JSON` writes `ui-plan.json`; `Write to DB` writes into
   `ui-plan.db`.
6. **Execute with the CLI** (the only thing that modifies files):
   ```bash
   dotnet run --project src/BackupNormalizer -- plan import ./ui-plan.json --db ./execution.db --target-path /Volumes/D1
   dotnet run --project src/BackupNormalizer -- execute <plan-id> --db ./execution.db
   dotnet run --project src/BackupNormalizer -- verify <plan-id> --db ./execution.db
   ```
   The executor re-validates every size/hash before acting, refuses to
   overwrite different content, and moves trash to
   `.backup-normalizer-trash/<plan-id>/` instead of deleting.

## Notes

- Plans are immutable and drive-local. Import the original JSON into a fresh
  execution database for each drive. First execution records its roots;
  resume uses those roots, and a later execute with different roots is rejected.
  Drifted content fails safely as a conflict.
- F7 folders, marks and cursor are UI-only state; rescans/refreshes rebuild
  listings from disk plus staged virtuals.

## Replay on an external drive

After organizing under `G:\Photos`, keep the exported JSON. To apply the same
relative operations under `E:\Photos`, choose a new database filename:

```powershell
dotnet run --project src/BackupNormalizer -- plan import .\ui-plan.json --db .\external-plan.db --target-path "E:\Photos"
dotnet run --project src/BackupNormalizer -- execute <plan-id> --db .\external-plan.db
dotnet run --project src/BackupNormalizer -- verify <plan-id> --db .\external-plan.db
```

The external drive must contain the expected starting files. Their sizes and
hashes are checked before applying each operation. A fresh import does not
reuse completed statuses from the PC's execution database.
