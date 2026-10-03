# Inventory health

Loading an inventory snapshot also loads the selected root's scan status, scan age, scan mode, fallback reason, entry counts, current hash coverage, planning readiness, and latest scan diagnostics. The health view reads through the database's read-only API. Older inventories remain browsable, with fields absent from their schema shown as unknown.

Each database panel shows a compact readiness summary. Select **Health / errors...** to inspect the selected root. The diagnostics list contains the latest scan's recorded paths, messages, and timestamps. Health information describes the loaded snapshot; refresh the panels after another process scans or hashes the database.

The content readiness status requires a complete scan and usable hashes. Automatic planning also enforces writable target roots and disjoint source and target paths.
