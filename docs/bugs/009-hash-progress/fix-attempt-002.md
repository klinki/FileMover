# Fix attempt 002

## Attempt status

`awaiting-user-confirmation` after local verification.

## Goal

Keep hashing progress readable when a container terminal reports no width.

## Relation to previous attempts

Attempt 001 remains verified for a normal Windows terminal. A follow-up ARMv7 container check exposed an additional terminal configuration.

## Findings

- `docker run -t` without a connected input terminal reproduces the problem.
- The initial progress line renders only `h`, followed by the complete final summary.
- The renderer clamps a reported zero console width to one character.

## Proposed change

Use a 120-column fallback when console width is zero or one. Preserve truncation for terminals that report a usable width.

## Risks

The fallback may wrap in a narrow log viewer. It preserves the progress content instead of silently truncating it to one character.

## Files and components

- CLI hash progress renderer
- NAS container image built from that CLI

## Verification plan

Run the existing CLI progress regression checks. Rebuild the image and repeat the zero-width terminal reproduction, checking that the full progress line appears.

## Implementation summary

The renderer now uses 120 columns when the terminal reports zero or one column. Existing terminal widths still control truncation. Rebuilt the ARMv7 image archive with this CLI.

## Test results

- Repeated the exact `docker run -t` reproduction with the rebuilt image. The progress line now shows `hash --needed: 0/0 files (100%)`, byte count, throughput, and elapsed time instead of only `h`.
- Seven focused hashing regression checks passed.
- Debug test compilation initially encountered stale Release UI dependency assets. Restoring the solution for Debug resolved the dependency mismatch without changing UI sources.

## Outcome

Locally verified in the container configuration. User confirmation remains pending.

## Next step

Request confirmation that hashing progress is readable when the user runs the rebuilt image on the NAS.

## Remaining gaps

User confirmation remains pending for the hash-progress bug. Actual NAS compatibility is tracked separately in the container feature.
