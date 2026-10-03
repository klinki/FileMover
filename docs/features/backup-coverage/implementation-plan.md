# Backup coverage across inventories

## Goal

Combine read-only inventory snapshots to find content recorded on only one device
and content recorded on every selected device. Show all recorded locations,
inventory age, and local root availability without reading or changing file data.

## Implementation

1. Add a core coverage report grouping regular files by size and usable full
   SHA-256 digest. Count distinct explicit device labels, not paths, roots, or
   database files. Exclude missing entries, links, linked descendants, and stored
   path exclusions. Report missing or stale hashes and incomplete inventories
   separately rather than claiming confirmed coverage.
1. Add a CLI command accepting repeated `--inventory DEVICE=DATABASE` arguments
   and JSON output. Every root in an input database belongs to its selected device.
1. Add an Inventory menu report window with multiple inventory selection, editable
   device labels, filtering, and recorded-location details. Analyze in the background.
1. Document interpretation and add tests for distinct-device counting, incomplete
   snapshots, stale hashes, exclusions, offline roots, and UI filtering.
1. Format, build, test, and commit this feature before starting desktop execution.

## Acceptance

- Duplicate paths or exported copies assigned to the same device count once.
- Equal content at different paths matches; equal sizes without hashes do not.
- Availability and age describe snapshots, not a live guarantee of backup health.
- Inventory databases remain read-only and do not receive migrations.
