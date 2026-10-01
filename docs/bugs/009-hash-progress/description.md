# Hash command has no live progress

## Status

Open; `awaiting-user-confirmation` after local verification.

## Reported symptoms

The user reports that hashing does not show progress while inventorying drive `G:\`.

## Expected behavior

Interactive hashing shows progress while files are read, including during a large file. The final summary remains available when output is redirected.

## Actual behavior

The CLI prints only after `HashNeeded` returns. Neither the scanner nor the content hasher exposes hashing progress.

## Reproduction details

Register and scan a root, then run `hash <root-id> --all --db <inventory.db>` in a terminal. No status appears until hashing finishes. `hash --needed` has the same behavior.

## Affected area

- `src/BackupNormalizer/Cli.cs`
- `src/BackupNormalizer.Core/Scanner.cs`
- `src/BackupNormalizer.Core/Hashing.cs`

## Constraints

- Preserve hashing results, cache reuse, database exclusion, and conservative parallelism.
- Support both root-specific hashing and `--needed`.
- Keep redirected output quiet and offer `--no-progress`.
- Do not access the user's drive for verification.

## Open questions

None required for implementation.
