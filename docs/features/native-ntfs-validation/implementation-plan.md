# Native NTFS validation plan

Approved on 2026-10-03 as item 1 of the inventory reliability work.

- Compare real USN updates with recursive and MFT inventories after file creation, same-size content writes with restored timestamps, renames, deletion, and link changes.
- Verify cached hashes against freshly computed reference hashes and record timings without imposing a machine-dependent speed threshold.
- Use isolated generated fixtures under the workspace. Never modify live inventories or create, reset, or delete a volume journal.
- Provide an elevated Windows runner with saved logs and explicit failure when native checks skip.
- Record the actual native results or the precise environment limitation. Commit this item separately.
