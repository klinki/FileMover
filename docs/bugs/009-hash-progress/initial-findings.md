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

## Container follow-up, 2026-10-01

After attempt 001, `docker run -t` with disconnected input showed an initial progress line containing only `h`. The renderer clamps a zero reported terminal width to one column. Attempt 002 adds a fallback width for this container configuration.
