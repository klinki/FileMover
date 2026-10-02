# Initial findings

## Confirmed facts

- Scanner reads `FileInfo.Length` before the reparse-point branch and counts retained file links as errors.
- Recursive and MFT enumeration omit directory links.
- Hashing and matching filter only by status. Manual staging hashes file links.
- Execution validates relative paths lexically and can access entries through linked parents.
- Database inventories mark every non-Ok entry as a scan error.
- Existing writable databases migrate automatically; read-only inventories do not migrate.

## Likely cause

The data model treats links as failures instead of separately identifying their entry kind.

## Unknowns

Real Windows symlink and elevated MFT availability will be checked during verification.

## Reproduction status

Confirmed by code inspection. Regression fixtures will cover actual links and synthetic MFT records.

## Evidence gathered

The baseline Release subset passed 21 tests. Debug compilation fails on the unrelated `WithDeveloperTools` reference.

## 2026-10-02 implementation evidence

Real Windows junctions, including missing targets and a cycle, scan successfully without linked descendants. Synthetic MFT checks emit directory links while excluding their descendants. The full Release suite passed 147 tests with 17 skipped. File-symlink creation and elevated volume access are unavailable, so the corresponding integration checks remain pending. See [fix attempt 001](fix-attempt-001.md) for verification details.
