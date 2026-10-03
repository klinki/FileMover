# Versioned portable releases plan

Approved on 2026-10-03 as item 4 of the inventory reliability work.

- Share application version 0.2.0 across CLI, core, and UI. Read build identity from assembly metadata rather than hard-coded CLI text.
- Show version, Git revision, configuration, runtime, and source-state provenance through CLI `--version`; expose version in the UI title and its headless `--version` path.
- Provide a repeatable self-contained release ZIP containing separate CLI/UI folders, a build manifest, and a SHA-256 checksum.
- Preserve unrelated NAS work. Determine dirty source state from application and packaging inputs, retain unknown provenance for ordinary unverified builds, and never label modified application sources as a clean commit build.
- Verify version output, UI startup bypass, both published executables, package contents, and checksum. Run the full Release suite and commit this item separately.
