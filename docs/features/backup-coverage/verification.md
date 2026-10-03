# Backup coverage verification

Implemented and checked on 2026-10-03.

- Six new tests passed for grouping, device identity, offline read-only analysis,
  unusable hashes, incomplete scans, exclusions, CLI JSON, and desktop filtering.
- Full Release suite passed: 315 passed, 20 skipped, 335 total.
- The skips are existing optional native filesystem and headless input tests.
- CSharpier checked 101 C# files successfully; `git diff --check` passed.
- The desktop report window was constructed and shown in the headless UI host.

Coverage is historical. Device labels are user declarations; input snapshots
cannot prove current drive contents or physical independence.
