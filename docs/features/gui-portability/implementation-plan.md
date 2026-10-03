# GUI portability and browsing sessions

Expose portable database snapshots through the File menu for the active database panel. Use the existing SQLite backup exporter, including refusal to replace existing files, so committed WAL contents are retained and source databases remain unchanged.

Persist the left and right panel sources, selected roots, folder paths, active side, divider position, and resizable column widths in a local browsing preferences file. Restore sources independently. Unavailable databases keep a live panel and produce an explained warning. Missing inventory folders fall back to their nearest indexed parent. Staged plans and filesystem operations are never restored automatically.

Label recorded inventory root paths separately from their availability on the current machine. Offline inventories remain browsable.

Verify JSON persistence, invalid preferences, unavailable sources, separate roots in a shared database, folder fallback, and portable export. The desktop app opts into persistence; view model tests never access real user preferences.
