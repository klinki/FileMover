# Portable inventory export plan

Approved on 2026-10-03 as item 3 of the inventory reliability work.

- Add `db export --db SOURCE --output DESTINATION` using the SQLite backup API rather than filesystem copying.
- Read without migrating the source, include committed WAL data, and publish one checked database file through a temporary file in the destination directory.
- Refuse existing destinations, source/companion collisions, and invalid paths. Clean up only this operation's temporary files on failure.
- Retain roots, scans, diagnostics, hashes, plans, and journal checkpoints exactly as inventory data. Do not remap roots or manufacture completeness.
- Verify live WAL snapshots, uncommitted writes, old schemas, content preservation, collision handling, and CLI output. Commit this item separately.
