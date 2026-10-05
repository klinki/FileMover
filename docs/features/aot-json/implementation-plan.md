# AOT-compatible JSON serialization

## Approved scope

Implement the JSON portion of the Native AOT recommendations. Generate serialization metadata during compilation for application JSON paths, preserve current JSON contracts, and disable reflection defaults in the CLI and Windows UI. Keep .NET 10 and the existing database implementation for this change.

## Implementation

1. Introduce source-generated contexts for core documents, CLI output, and GUI state.
1. Replace anonymous JSON output with named records and replace untyped logging fields with explicitly serialized fields.
1. Convert configuration, plan import/export, exclusion policies, reports, CLI JSON, GUI session persistence, and inventory drag payloads to generated metadata.
1. Preserve property names, null values, computed report properties, indentation, configuration validation, and existing import case sensitivity and unknown-property handling.
1. Disable default reflection serialization for the CLI and UI so missing registrations fail in ordinary builds.

## Verification

- Run existing configuration, plan import/export, report, CLI JSON, exclusion policy, GUI session, and inventory drag tests.
- Add focused compatibility checks for generated JSON and structured logging.
- Build CLI and UI; run CLI configuration and JSON commands with reflection defaults disabled.
- Publish and run an isolated Native AOT JSON probe using the actual serializers if the existing Linux cross-build tools remain available. Full EF-backed AOT execution is outside this change.

## Delivery

Record validation in a verification document in this folder. Preserve unrelated workspace edits. No commit, push, .NET 11 installation, or full database AOT migration is requested.

## Completion

Implemented. [Verification](verification.md) records successful builds, the full test suite with reflection disabled, CLI process checks, and the actual QNAP Native AOT JSON probe.
