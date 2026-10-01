# Initial findings

## Confirmed facts

- `Cli.Hash` calls `Scanner.HashNeeded` and prints only its return value.
- `HashNeeded` runs parallel file reads without a progress observer.
- SHA-256 reads files in 4 MiB chunks, which can provide updates before a file completes.
- Existing scanning progress is disabled when stdout is redirected.

## Likely cause

Hashing progress was never implemented; this is independent of SQLite and drive mapping.

## Unknowns

The duration and file sizes on the user's drive are unknown. Generated files are sufficient to verify reporting without reading that drive.

## Reproduction status

Confirmed by inspecting the CLI and hashing call chain. The regression checks will exercise the missing observer and interactive output.

## Evidence gathered

- No progress parameter on `HashNeeded`, `HashFile`, or `HashStream`.
- The hash CLI has only completion summaries and no progress renderer.
