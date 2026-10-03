# Bug description

## Title

Planning errors for unhashed source files omit the drive and database.

## Status

`awaiting-user-confirmation`

## Reported symptoms

On 2026-10-03, planning from source root `g` in `g.db` to target root `d`
in `d.db` failed for an unhashed file. The error included only the relative
path `DriverData/dld/stash/generated/thumbnails/hybrid/159.db`, leaving the
drive unclear.

## Expected behavior

The error identifies the source root ID, its filesystem path, the source
database, and the affected relative path.

## Actual behavior

The planner reports the relative path and asks the user to hash the source
root, without naming the root or its filesystem path.

## Reproduction details

Scan a source root containing a regular file without computing its full hash.
Scan a disjoint writable target root, then plan between the databases.

## Affected area

[Planner](../../../src/BackupNormalizer.Core/Planner.cs) and CLI plan errors.

## Constraints

Preserve hash requirements and unrelated changes. Do not alter the user's
databases. Explain how exclusions apply to inventories that already exist.

## Open questions

The reason the reported file lacks a usable hash has not been established.
No inspection of the user's external databases is needed for this repair.
