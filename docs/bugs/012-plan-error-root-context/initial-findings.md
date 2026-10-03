# Initial findings

## Confirmed facts

- The unhashed-source exception includes only `want.RelativePath`.
- The planner already has the selected source root ID, stored root path, and
  source database path available.
- Plans read exclusion rules stored per root in each database. They do not
  apply new configuration exclusions directly.
- A scan with changed exclusions forces full enumeration. After a successful
  scan, previously inventoried excluded files have status `Missing` while
  their files remain on disk.
- Hashing respects additional configuration exclusions but does not persist
  them as the planning scope. Applying rules to an existing database requires
  a successful rescan.

## Likely cause

The exception omitted existing root context when it was written.

## Unknowns

The user's source root may have files that were never hashed, changed since
hashing, or failed during hashing. This message does not distinguish those
causes.

## Reproduction status

The existing planner test reproduces failure for an unhashed source.
Add assertions for context and a regression covering exclusions applied to
an existing inventory.

## Evidence gathered

- [Planner](../../../src/BackupNormalizer.Core/Planner.cs)
- [Scanner](../../../src/BackupNormalizer.Core/Scanner.cs)
- [Persisted exclusions](../../../src/BackupNormalizer.Core/Database.Exclusions.cs)
- [Configuration guide](../../features/cli-config-exclusions/README.md)
