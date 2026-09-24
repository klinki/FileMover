# BackupNormalizer.Ui — Total Commander style planner (plan-only)

A cross-platform (Windows / macOS / Linux) Avalonia UI for staging
BackupNormalizer plans. It never touches your files: every gesture only
records an operation with size+hash preconditions. The existing CLI
executor applies the plan later, with full verification.

## Run

```bash
dotnet run --project src/BackupNormalizer.Ui
```

Requires the .NET 10 SDK. No Docker needed (Docker is only for the QNAP
scanner, see the repo README).

## Basic usage

1. **Set the base.** `Base (drive-local)` is the logical root everything is
   staged against (e.g. `/Volumes/D1`). Press **Apply**. All staged paths
   are stored relative to the `Root` id (default `disk`), so the same plan
   can later run on another drive via the CLI's `--map-root`.
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
5. **Export.** `Save JSON` writes `ui-plan.json`; `Write to DB` writes into
   `ui-plan.db`.
6. **Execute with the CLI** (the only thing that modifies files):
   ```bash
   dotnet run --project src/BackupNormalizer -- plan import ./ui-plan.json --db ./ui-plan.db --root-path /Volumes/D1
   dotnet run --project src/BackupNormalizer -- execute <plan-id> --db ./ui-plan.db --map-root disk=/Volumes/D1
   dotnet run --project src/BackupNormalizer -- verify <plan-id> --db ./ui-plan.db --map-root disk=/Volumes/D1
   ```
   The executor re-validates every size/hash before acting, refuses to
   overwrite different content, and moves trash to
   `.backup-normalizer-trash/<plan-id>/` instead of deleting.

## Notes

- Plans are immutable and drive-local; replaying on an identical drive is
  just `--map-root`, drifted content fails safely as a conflict.
- F7 folders, marks and cursor are UI-only state; rescans/refreshes rebuild
  listings from disk plus staged virtuals.
