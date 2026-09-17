# RouteResolutionClient migration pilot

`RouteResolutionClient` is a useful first migration target because its public behavior is compact while its runtime
dependencies exercise the difficult parts of a Substrate migration: gRPC contracts, certificate loading, ECS-backed
configuration, diagnostics, retries, and TDS deployment.

## Dependency strategy

Do not wait for every C# SDK to gain a Rust package, and do not silently replace missing SDKs with mocks. Put each
dependency behind a narrow Rust trait and choose one explicit strategy:

| Strategy | Use |
|---|---|
| Rust native | A supported Rust library provides the required production behavior. |
| Generated | Generate Rust code from the same protocol or schema source. |
| Bridge | Keep the existing platform implementation behind a small FFI, IPC, or host-service boundary during migration. |
| Test double | Isolated unit tests only; never evidence for TDS readiness. |
| Unavailable | The dependency blocks runtime verification and the verdict is `blocked`, not `failed`. |

For this client, protobuf messages and service contracts should be generated from the shared schema. Readiness evidence
must hash both the generated artifact and the exact source schema so a stale generated artifact cannot remain ready
after the schema changes. The current client uses gRPC-Web, so the Rust transport remains blocked until a compatibility
spike validates an approved client or a C# transport bridge. CredSMART, M365 environment/security abstractions, ECS
configuration, and RoutingPlane diagnostics should initially be explicit compatibility boundaries. This allows the core
request, retry, response-mapping, and error behavior to move to Rust without pretending the surrounding Substrate
platform already has Rust equivalents.

The tracked dependency decisions are in `targets\route-resolution-client.json`.

The current Rust deployment adapter is deliberately marked unavailable in
`targets\route-resolution-client-tds-adapter.json`. The existing Substrate TDS skill can establish the C# baseline and
run Route Resolution scenarios, but it does not yet define how to build, deploy, activate, health-check, or roll back a
Rust artifact. End-to-end verification must remain blocked until those fields are implemented.

The unprivileged preflight pins command-bearing Substrate instructions to the canonical repository and immutable commit
recorded in the environment profile. Only a successful HMAC-authenticated attestation may be handed to a separate
TDS-enabled executor, which must verify it with an independently configured key and rerun preflight immediately before
mutation. Updating the instruction pin, environment profile, or target manifest mapping is an explicit review action,
not a runtime input.

## Verification layers

1. **Contract tests** compare public inputs, outputs, response-code mappings, retries, cancellation, and errors between
   the C# baseline and Rust candidate.
2. **Generated-contract tests** compare protobuf wire compatibility using shared fixtures from `RouteResolution.proto`.
3. **Adapter tests** exercise each bridge against its real TDS capability, especially certificate lookup, ECS updates,
   and telemetry emission.
4. **Shadow TDS tests** run the C# client as the oracle and the Rust candidate against the same routing scenarios, then
   compare normalized results.
5. **Replacement readiness** requires all runtime dependencies to be native, generated, or exercised bridges.
   Unit-test-only mocks cannot satisfy this gate.

Substrate already contains `sources\dev\cafe\src\.github\skills\RouteResolutionTdsTest\SKILL.md`, which owns the current
Route Resolution Build -> Deploy -> Test -> Diagnose workflow. The agent team should delegate to that skill instead of
duplicating TDS commands.
